using CarSpec.Constants;
using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Obd;
using CarSpec.Services.Telemetry;
using CarSpec.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Timers;

namespace CarSpec.Components.Pages.Dashboard
{
    public partial class Dashboard : ComponentBase, IAsyncDisposable
    {
        [Inject] public ObdConnectionService ObdService { get; set; } = default!;
        [Inject] public IJSRuntime JS { get; set; } = default!;
        [Inject] public IVehicleProfileService Profiles { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;
        [Inject] public IAppStorage Storage { get; set; } = default!;
        [Inject] public RecordingService Recorder { get; set; } = default!;
        [Inject] public ReplayService Replayer { get; set; } = default!;

        private CarData? carData;
        private readonly List<string> outputLog = new();

        private enum DisplayMode { Gauges, Numbers }
        private DisplayMode _view = DisplayMode.Gauges;

        private enum DashboardLayoutMode
        {
            Basic,
            Enthusiast,
            Custom
        }

        private DashboardLayoutMode _layoutMode = DashboardLayoutMode.Basic;

        private static readonly string[] BasicGaugePids =
        {
            PID_RPM,
            PID_SPEED,
            PID_COOLANT,
            PID_FUEL
        };

        private HashSet<string> _customVisiblePids = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _replayAvailablePids = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Func<CarData?, object?>> _pidAccessors =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { PID_RPM,       cd => cd?.RPM },
                { PID_SPEED,     cd => cd?.Speed },
                { PID_TPS,       cd => cd?.ThrottlePercent },
                { PID_FUEL,      cd => cd?.FuelLevelPercent },
                { PID_COOLANT,   cd => cd?.CoolantTempF },
                { PID_OIL,       cd => cd?.OilTempF },
                { PID_IAT,       cd => cd?.IntakeTempF },
                { PID_LOAD,      cd => cd?.EngineLoadPercent },
                { PID_FPRESS,    cd => cd?.FuelPressureKpa },
                { PID_MAP,       cd => cd?.MapKpa },
                { PID_TADV,      cd => cd?.TimingAdvanceDeg },
                { PID_MAF,       cd => cd?.MafGramsPerSec },
                { PID_BARO,      cd => cd?.BaroKPa },
                { PID_FRP_REL,   cd => cd?.FuelRailPressureRelKPa },
                { PID_FRP_GAUGE, cd => cd?.FuelRailGaugePressureKPa },
                { PID_EGR_CMD,   cd => cd?.CommandedEgrPercent },
                { PID_EGR_ERR,   cd => cd?.EgrErrorPercent },
                { PID_EVAP_CMD,  cd => cd?.CommandedEvapPurgePercent },
                { PID_EVAP_P,    cd => cd?.EvapVaporPressurePa },
                { PID_WARMUPS,   cd => cd?.WarmUpsSinceClear },
                { PID_DIST_MIL,  cd => cd?.DistanceWithMilKm },
                { PID_DIST_CLR,  cd => cd?.DistanceSinceClearKm }
            };

        private static bool IsBasicPid(string pid) =>
            Array.IndexOf(BasicGaugePids, pid) >= 0;

        private bool IsLayout(DashboardLayoutMode mode) => _layoutMode == mode;

        private sealed class CustomMetricOption
        {
            public string Pid { get; init; } = "";
            public string Label { get; init; } = "";
        }

        private IEnumerable<CustomMetricOption> GetCustomCandidates()
        {
            var all = new List<CustomMetricOption>
            {
                new() { Pid = PID_RPM,        Label = "RPM" },
                new() { Pid = PID_SPEED,      Label = "Speed" },
                new() { Pid = PID_TPS,        Label = "Throttle" },
                new() { Pid = PID_FUEL,       Label = "Fuel" },
                new() { Pid = PID_COOLANT,    Label = "Coolant" },
                new() { Pid = PID_OIL,        Label = "Oil Temp" },
                new() { Pid = PID_IAT,        Label = "Intake Air" },
                new() { Pid = PID_LOAD,       Label = "Engine Load" },
                new() { Pid = PID_FPRESS,     Label = "Fuel Pressure" },
                new() { Pid = PID_MAP,        Label = "MAP" },
                new() { Pid = PID_TADV,       Label = "Timing" },
                new() { Pid = PID_MAF,        Label = "MAF" },
                new() { Pid = PID_BARO,       Label = "Barometric Pressure" },
                new() { Pid = PID_FRP_REL,    Label = "Fuel Rail (rel)" },
                new() { Pid = PID_FRP_GAUGE,  Label = "Fuel Rail (gauge)" },
                new() { Pid = PID_EGR_CMD,    Label = "Cmd EGR" },
                new() { Pid = PID_EGR_ERR,    Label = "EGR Error" },
                new() { Pid = PID_EVAP_CMD,   Label = "Cmd EVAP Purge" },
                new() { Pid = PID_EVAP_P,     Label = "EVAP Vapor Pressure" },
                new() { Pid = PID_WARMUPS,    Label = "Warm-ups" },
                new() { Pid = PID_DIST_MIL,   Label = "Distance w/ MIL" },
                new() { Pid = PID_DIST_CLR,   Label = "Distance since clear" }
            };

            return all.Where(opt => IsMetricAvailable(opt.Pid));
        }

        private bool IsCustomOn(string pid)
        {
            if (_customVisiblePids.Count == 0 && _layoutMode == DashboardLayoutMode.Custom)
                return IsBasicPid(pid);

            return _customVisiblePids.Contains(pid);
        }

        private async Task OnCustomToggle(string pid, ChangeEventArgs e)
        {
            bool isOn = e?.Value is bool b && b;

            ToggleCustomPid(pid, isOn);
            await SaveDashboardPrefsAsync();

            if (_view == DisplayMode.Gauges)
            {
                gaugeReady = false;
                await DisposeGaugesAsync(all: true);
                StateHasChanged();
                await Task.Yield();
                await EnsureGaugesInitializedAsync();
            }
            else
            {
                StateHasChanged();
            }
        }

        private async Task ChangeLayout(DashboardLayoutMode mode)
        {
            if (_layoutMode == mode) return;

            _layoutMode = mode;

            if (mode == DashboardLayoutMode.Custom && _customVisiblePids.Count == 0)
            {
                foreach (var pid in BasicGaugePids)
                    _customVisiblePids.Add(pid);
            }

            await SaveDashboardPrefsAsync();

            if (_view == DisplayMode.Gauges)
            {
                gaugeReady = false;
                await DisposeGaugesAsync(all: true);
                StateHasChanged();
                await Task.Yield();
                await EnsureGaugesInitializedAsync();
            }
            else
            {
                StateHasChanged();
            }
        }

        private void ToggleCustomPid(string pid, bool isOn)
        {
            if (isOn) _customVisiblePids.Add(pid);
            else _customVisiblePids.Remove(pid);
        }
        // === END layout bits ===

        // --- Dashboard per-profile preferences ---
        private sealed class DashboardPreferences
        {
            public string ViewMode { get; set; } = nameof(DisplayMode.Gauges);
            public string LayoutMode { get; set; } = nameof(DashboardLayoutMode.Enthusiast);
            public List<string>? CustomPids { get; set; }
        }

        private const string DashPrefsPrefix = "dashboard:prefs:";

        private string GetPrefsKey()
        {
            var p = Profiles?.Current;
            if (p is null) return "";

            if (!string.IsNullOrWhiteSpace(p.Id))
                return $"{DashPrefsPrefix}{p.Id}";

            return $"{DashPrefsPrefix}{p.Year}|{p.Make}|{p.Model}";
        }

        private async Task LoadDashboardPrefsAsync()
        {
            var key = GetPrefsKey();
            if (string.IsNullOrWhiteSpace(key)) return;

            try
            {
                var prefs = await Storage.GetAsync<DashboardPreferences>(key);
                if (prefs is null) return;

                if (Enum.TryParse<DisplayMode>(prefs.ViewMode, out var view))
                    _view = view;

                if (Enum.TryParse<DashboardLayoutMode>(prefs.LayoutMode, out var layout))
                    _layoutMode = layout;

                if (prefs.CustomPids is { Count: > 0 })
                    _customVisiblePids = new HashSet<string>(prefs.CustomPids, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* ignore */}
        }

        private async Task SaveDashboardPrefsAsync()
        {
            var key = GetPrefsKey();
            if (string.IsNullOrWhiteSpace(key)) return;

            var prefs = new DashboardPreferences
            {
                ViewMode = _view.ToString(),
                LayoutMode = _layoutMode.ToString(),
                CustomPids = _customVisiblePids.Count > 0
                    ? new List<string>(_customVisiblePids)
                    : null
            };

            try
            {
                await Storage.SetAsync(key, prefs);
            }
            catch { /* ignore */ }
        }

        private bool _needsSetup;
        private bool _attachInProgress;
        private Action<EcuFingerprint?>? _fpHandler;
        private Action? _stateHandler;
        private Task _pendingAttach = Task.CompletedTask;
        private bool _isRecording;
        private string? _currentRecordingId;
        private List<RecordingMeta> _replays = new();
        private string _selectedReplayId = "";
        private bool _isReplaying;
        private bool _isReplayPaused;
        private double _replaySpeed = 1.0;
        private long? _replayPositionMs;
        private long? _replayDurationMs;
        private System.Timers.Timer? _replayTimer;
        private DateTime _replayStartUtc;

        private bool IsLive => ObdService.IsEcuConnected && !ObdService.SimulationMode;
        private bool ShouldRenderGauges => _isReplaying || (IsLive && _planKnown);
        private bool _isPreparingReplay;

        private IJSObjectReference? gaugeModule;
        private bool gaugeReady;

        private IJSObjectReference? downloadModule;

        private void RunSetup() => Nav.NavigateTo("/setup");

        private HashSet<string> _plan = new(StringComparer.OrdinalIgnoreCase);
        private bool _planKnown => _plan.Count > 0;

        private string CurrentVehicleLabel =>
            Profiles?.Current is null
                ? ""
                : $"{Profiles.Current.Year} {Profiles.Current.Make} {Profiles.Current.Model}".Trim();

        private IReadOnlyList<RecordingMeta> FilteredReplays
        {
            get
            {
                if (_replays is null || _replays.Count == 0)
                    return Array.Empty<RecordingMeta>();

                var currentLabel = Profiles?.Current is VehicleProfile vp
                    ? $"{vp.Year} {vp.Make} {vp.Model}"
                    : null;

                return _replays
                    .Where(r =>
                        string.IsNullOrWhiteSpace(currentLabel)
                            ? true
                            : string.Equals(r.Vehicle, currentLabel, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.CreatedUtc)
                    .ToList();
            }
        }

        /// <summary>
        /// Whether this metric is actually available in the current context
        /// (live poll plan or current recording).
        /// </summary>
        private bool IsMetricAvailable(string pid)
        {
            if (_isReplaying)
            {
                if (_replayAvailablePids.Count == 0)
                    return IsBasicPid(pid);

                return _replayAvailablePids.Contains(pid);
            }

            if (_planKnown)
                return _plan.Contains(pid);

            var profilePids = Profiles?.Current?.SupportedPidsCache;
            if (profilePids is { Count: > 0 })
                return profilePids.Contains(pid);

            return IsBasicPid(pid);
        }

        private bool CanShow(string pid)
        {
            if (!IsMetricAvailable(pid))
                return false;

            if (_isReplaying)
            {
                return _layoutMode switch
                {
                    DashboardLayoutMode.Basic => IsBasicPid(pid),
                    DashboardLayoutMode.Enthusiast => true,
                    DashboardLayoutMode.Custom =>
                        _customVisiblePids.Count == 0
                            ? IsBasicPid(pid)
                            : _customVisiblePids.Contains(pid),
                    _ => true
                };
            }

            return _layoutMode switch
            {
                DashboardLayoutMode.Basic => IsBasicPid(pid),
                DashboardLayoutMode.Enthusiast => true,
                DashboardLayoutMode.Custom =>
                    _customVisiblePids.Count == 0
                        ? IsBasicPid(pid)
                        : _customVisiblePids.Contains(pid),
                _ => true
            };
        }

        private const string PID_RPM = "010C";
        private const string PID_SPEED = "010D";
        private const string PID_TPS = "0111";
        private const string PID_FUEL = "012F";
        private const string PID_COOLANT = "0105";
        private const string PID_IAT = "010F";
        private const string PID_OIL = "015C";
        private const string PID_LOAD = "0104";
        private const string PID_FPRESS = "010A";
        private const string PID_MAP = "010B";
        private const string PID_TADV = "010E";
        private const string PID_MAF = "0110";
        private const string PID_DIST_MIL = "0121";
        private const string PID_FRP_REL = "0122";
        private const string PID_FRP_GAUGE = "0123";
        private const string PID_EGR_CMD = "012C";
        private const string PID_EGR_ERR = "012D";
        private const string PID_EVAP_CMD = "012E";
        private const string PID_WARMUPS = "0130";
        private const string PID_DIST_CLR = "0131";
        private const string PID_EVAP_P = "0132";
        private const string PID_BARO = "0133";

        private string ProfileKey(VehicleProfile? v) => v is null ? "" : $"{v.Year}|{v.Make}|{v.Model}";
        private string _lastProfileKey = "";

        private int GetTachMax() => Profiles?.Current?.TachMaxRpm ?? 9000;
        private int GetRedlineStart() => Profiles?.Current?.TachRedlineStart ?? 7000;
        private int GetSpeedMax() => Profiles?.Current?.SpeedMaxMph ?? 160;

        private string[] BuildTachMajorTicks(int tachMax)
        {
            var steps = Math.Max(1, tachMax / 1000);
            var labels = new List<string>(steps + 1);
            for (int k = 0; k <= steps; k++) labels.Add(k.ToString());
            return labels.ToArray();
        }

        private string[] BuildSpeedMajorTicks(int speedMax)
        {
            var labels = new List<string>();
            for (int s = 0; s <= speedMax; s += 20) labels.Add(s.ToString());
            return labels.ToArray();
        }

        protected override async Task OnInitializedAsync()
        {
            try { outputLog.AddRange(ObdService.GetLogSnapshot()); } catch { }
            ObdService.OnLog += HandleLog;

            _fpHandler = _ => InvokeAsync(StateHasChanged);
            ObdService.OnFingerprint += _fpHandler;

            _stateHandler = () => _ = OnServiceStateChangedAsync();
            ObdService.OnStateChanged += _stateHandler;

            ObdService.OnData += HandleLiveData;

            ObdService.OnReplayCompleted += HandleReplayCompleted;

            Log("🚀 CarSpec Dashboard Successfully Started...");

            await RecomputeNeedsSetupAsync();
            _lastProfileKey = ProfileKey(Profiles?.Current);

            await LoadDashboardPrefsAsync();

            try { carData = ObdService.LastSnapshot ?? await ObdService.GetLatestDataAsync(); } catch { }
            try { _replays = await Recorder.ListAsync(); } catch { _replays = new(); }

            if (!_needsSetup && ObdService.IsEcuConnected && !ObdService.SimulationMode)
                await AttachLiveStreamAsync();
        }

        private void HandleReplayCompleted()
        {
            _isReplaying = false;
            _isReplayPaused = false;
            _replayTimer?.Stop();
            _replayPositionMs = _replayDurationMs;
            Log("⏹️ Replay finished.");
            InvokeAsync(StateHasChanged);
        }

        private async Task RecomputeNeedsSetupAsync()
        {
            var hasVehicleKey = await Storage.HasAsync(AppKeys.VehicleProfile);
            var hasCompleted = await Storage.HasAsync(AppKeys.SetupCompleted);

            try { await Profiles.LoadAsync(); } catch { /* ignore */ }
            var hasCurrentProfile = Profiles?.Current != null;

            _needsSetup = !hasCurrentProfile || !hasVehicleKey || !hasCompleted;
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await EnsureGaugesInitializedAsync();

                if (ObdService.IsEcuConnected && !ObdService.SimulationMode)
                    await AttachLiveStreamAsync();
            }
        }

        private async Task OnServiceStateChangedAsync()
        {
            await InvokeAsync(StateHasChanged);

            if (ObdService.IsEcuConnected && !ObdService.SimulationMode && !_attachInProgress)
            {
                if (_pendingAttach.IsCompleted)
                    _pendingAttach = AttachLiveStreamAsync();
            }
        }

        private bool UpdateReplayAvailablePids(CarData? cd)
        {
            if (cd is null) return false;

            bool changed = false;

            foreach (var kvp in _pidAccessors)
            {
                var value = kvp.Value(cd);
                if (value is null) continue;

                bool hasValue = value switch
                {
                    int i => i != 0,
                    long l => l != 0,
                    short s => s != 0,
                    byte b => b != 0,
                    float f => Math.Abs(f) > float.Epsilon,
                    double d => Math.Abs(d) > double.Epsilon,
                    _ => false
                };

                if (hasValue)
                {
                    if (_replayAvailablePids.Add(kvp.Key))
                        changed = true;
                }
            }

            return changed;
        }

        private async void HandleLiveData(CarData cd)
        {
            if (_isRecording)
            {
                try { await Recorder.AppendAsync(cd); } catch { }
            }

            HashSet<string> newPlan = _plan;
            bool planChanged = false;

            if (!_isReplaying)
            {
                newPlan = (ObdService.CurrentPollPlan ?? Array.Empty<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                planChanged = !_planKnown || !_plan.SetEquals(newPlan);
            }

            await InvokeAsync(async () =>
            {
                if (_isReplaying)
                {
                    var pidsChanged = UpdateReplayAvailablePids(cd);
                    if (pidsChanged && _view == DisplayMode.Gauges)
                    {
                        gaugeReady = false;
                        StateHasChanged();
                        await Task.Yield();
                        await EnsureGaugesInitializedAsync();
                    }
                }
                else if (planChanged)
                {
                    _plan = newPlan;
                    if (_view == DisplayMode.Gauges)
                    {
                        gaugeReady = false;
                        StateHasChanged();
                        await Task.Yield();
                        await EnsureGaugesInitializedAsync();
                    }
                }

                carData = cd;

                await SafeSetAllGaugesAsync();
                StateHasChanged();
            });
        }

        private async Task StartRecording()
        {
            if (_isReplaying) return;

            if (!ObdService.IsEcuConnected || ObdService.SimulationMode)
            {
                Log("⚠️ Cannot start recording — ECU not live.");
                return;
            }

            try
            {
                var id = await Recorder.StartAsync(Profiles?.Current, ObdService.LastFingerprint, note: null);
                _currentRecordingId = id;
                _isRecording = true;
                Log($"⏺️ Recording started (id={id}).");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to start recording: {ex.Message}");
            }
        }

        private async Task StopRecording()
        {
            if (!_isRecording) return;
            try
            {
                var meta = await Recorder.StopAsync(gzip: true);
                _isRecording = false;
                Log($"⏹️ Recording saved ({meta?.Frames ?? 0} frames, {FormatMs(meta?.DurationMs)})");
                _replays = await Recorder.ListAsync();
                if (!string.IsNullOrWhiteSpace(meta?.Id))
                    _selectedReplayId = meta!.Id;
                StateHasChanged();
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to stop/save recording: {ex.Message}");
                _isRecording = false;
            }
        }

        private Task OnReplaySelectionChanged(string id)
        {
            _selectedReplayId = id;
            return Task.CompletedTask;
        }

        private Task OnReplaySpeedChanged(double speed)
        {
            _replaySpeed = speed <= 0 ? 1.0 : speed;
            return Task.CompletedTask;
        }

        private async Task StartReplay()
        {
            if (ObdService.IsEcuConnected && !ObdService.SimulationMode)
            {
                Log("⚠️ Cannot start replay while Live ECU is connected.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedReplayId) || _isPreparingReplay)
                return;

            _isPreparingReplay = true;
            _isReplayPaused = false;
            StateHasChanged();

            if (_isRecording)
                await StopRecording();

            await ObdService.Disconnect();
            ObdService.SimulationMode = true;

            _isReplaying = true;
            _replayAvailablePids.Clear();

            var profilePids = Profiles?.Current?.SupportedPidsCache;
            if (profilePids is { Count: > 0 })
            {
                foreach (var pid in profilePids)
                    _replayAvailablePids.Add(pid);
            }
            else
            {
                foreach (var pid in _pidAccessors.Keys)
                    _replayAvailablePids.Add(pid);
            }

            StateHasChanged();

            if (_view == DisplayMode.Gauges)
            {
                gaugeReady = false;
                await DisposeGaugesAsync(all: true);
                await ZeroOutDataAsync();
                StateHasChanged();
                await Task.Yield();
                await EnsureGaugesInitializedAsync();
            }

            var meta = _replays.FirstOrDefault(r => r.Id == _selectedReplayId);
            _replayDurationMs = meta?.DurationMs ?? 0;
            _replayPositionMs = 0;

            double speed = _replaySpeed <= 0 ? 1.0 : _replaySpeed;
            _replayStartUtc = DateTime.UtcNow;

            _replayTimer?.Dispose();
            _replayTimer = new System.Timers.Timer(200);
            _replayTimer.AutoReset = true;
            _replayTimer.Elapsed += (_, __) =>
            {
                if (!_isReplaying || _isReplayPaused) return;

                var elapsedRealMs = (DateTime.UtcNow - _replayStartUtc).TotalMilliseconds;
                var scaled = elapsedRealMs * speed;
                var pos = (long)scaled;

                if (_replayDurationMs is long dur && pos > dur)
                    pos = dur;

                _replayPositionMs = pos;
                _ = InvokeAsync(StateHasChanged);
            };
            _replayTimer.Start();

            _isPreparingReplay = false;
            StateHasChanged();

            Log($"▶️ Starting replay {_selectedReplayId} at {speed:0.##}×");
            await ObdService.StartReplayAsync(_selectedReplayId, speed);
        }

        private Task StopReplay()
        {
            if (!_isReplaying) return Task.CompletedTask;

            ObdService.StopReplay();
            _isReplaying = false;
            _isReplayPaused = false;
            _replayTimer?.Stop();
            _replayPositionMs = 0;
            Log("⏹️ Replay stopped.");
            return Task.CompletedTask;
        }

        private Task PauseReplay()
        {
            if (!_isReplaying || _isReplayPaused) return Task.CompletedTask;

            Replayer.Pause();
            _isReplayPaused = true;
            _replayTimer?.Stop();
            Log("⏸ Replay paused.");
            StateHasChanged();
            return Task.CompletedTask;
        }

        private Task ResumeReplay()
        {
            if (!_isReplaying || !_isReplayPaused) return Task.CompletedTask;

            Replayer.Resume();
            _isReplayPaused = false;

            var currentPos = _replayPositionMs ?? 0;
            var speed = _replaySpeed <= 0 ? 1.0 : _replaySpeed;

            _replayStartUtc = DateTime.UtcNow.AddMilliseconds(-(currentPos / speed));

            _replayTimer?.Start();
            Log("▶ Replay resumed.");
            StateHasChanged();
            return Task.CompletedTask;
        }

        private async Task RewindReplay()
        {
            if (!_isReplaying) return;

            var current = _replayPositionMs ?? 0;
            var target = current - 5000;
            if (target < 0) target = 0;

            var speed = _replaySpeed <= 0 ? 1.0 : _replaySpeed;
            _replayStartUtc = DateTime.UtcNow.AddMilliseconds(-(target / speed));
            _replayPositionMs = target;

            await Replayer.RewindAsync(5);
        }

        private async Task FastForwardReplay()
        {
            if (!_isReplaying) return;

            var duration = _replays.FirstOrDefault(r => r.Id == _selectedReplayId)?.DurationMs ?? 0;
            var current = _replayPositionMs ?? 0;
            var target = current + 5000;
            if (target > duration) target = duration;

            var speed = _replaySpeed <= 0 ? 1.0 : _replaySpeed;
            _replayStartUtc = DateTime.UtcNow.AddMilliseconds(-(target / speed));
            _replayPositionMs = target;

            await Replayer.FastForwardAsync(5, duration);
        }

        private async Task DeleteReplayAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (_isReplaying && _selectedReplayId == id)
            {
                ObdService.StopReplay();
                _isReplaying = false;
                _isReplayPaused = false;
            }

            try
            {
                await Recorder.DeleteAsync(id);

                _replays = await Recorder.ListAsync();

                if (_selectedReplayId == id)
                {
                    _selectedReplayId = _replays.LastOrDefault()?.Id ?? "";
                }

                Log($"🗑 Recording '{id}' deleted.");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to delete recording: {ex.Message}");
            }

            await InvokeAsync(StateHasChanged);
        }

        private async Task EnsureGaugesInitializedAsync()
        {
            if (gaugeReady || _view != DisplayMode.Gauges || !ShouldRenderGauges) return;

            gaugeModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/carspec.gauges.js");

            await DisposeGaugesAsync(all: true);

            if (CanShow(PID_RPM))
            {
                var tachMax = GetTachMax();
                var redStart = GetRedlineStart();
                var ticks = BuildTachMajorTicks(tachMax);
                await gaugeModule.InvokeVoidAsync("initGauge", "rpmGauge", new
                {
                    type = "radial",
                    value = carData?.RPM ?? 0,
                    minValue = 0,
                    maxValue = tachMax,
                    startAngle = 46,
                    ticksAngle = 270,
                    units = "rpm",
                    majorTicks = ticks,
                    minorTicks = 5,
                    highlights = new[] { new { from = redStart, to = tachMax, color = "rgba(255,0,0,.28)" } },
                    noResize = true
                });
            }

            if (CanShow(PID_SPEED))
            {
                var speedMax = GetSpeedMax();
                var speedTicks = BuildSpeedMajorTicks(speedMax);
                await gaugeModule.InvokeVoidAsync("initGauge", "speedGauge", new
                {
                    type = "radial",
                    value = carData?.Speed ?? 0,
                    minValue = 0,
                    maxValue = speedMax,
                    units = "mph",
                    majorTicks = speedTicks,
                    minorTicks = 2,
                    highlights = Array.Empty<object>()
                });
            }

            if (CanShow(PID_TPS))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "throttleGauge", new
                {
                    type = "linear",
                    value = carData?.ThrottlePercent ?? 0,
                    minValue = 0,
                    maxValue = 100,
                    valueSuffix = "%",
                    linearOverrides = new { needleSide = "right" }
                });
            }

            if (CanShow(PID_FUEL))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "fuelGauge", new
                {
                    type = "linear",
                    value = carData?.FuelLevelPercent ?? 0,
                    minValue = 0,
                    maxValue = 100,
                    valueSuffix = "%",
                    linearOverrides = new { needleSide = "right" }
                });
            }

            if (CanShow(PID_COOLANT))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "coolantGauge", new
                {
                    type = "radial",
                    value = carData?.CoolantTempF ?? 0,
                    minValue = 120,
                    maxValue = 260,
                    units = "°F",
                    majorTicks = new[] { "120", "160", "180", "200", "220", "240", "260" }
                });
            }

            if (CanShow(PID_OIL))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "oilGauge", new
                {
                    type = "radial",
                    value = carData?.OilTempF ?? 0,
                    minValue = 120,
                    maxValue = 300,
                    units = "°F",
                    majorTicks = new[] { "120", "160", "180", "200", "220", "240", "260", "280", "300" }
                });
            }

            if (CanShow(PID_IAT))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "iatGauge", new
                {
                    type = "radial",
                    value = carData?.IntakeTempF ?? 0,
                    minValue = 0,
                    maxValue = 200,
                    units = "°F",
                    majorTicks = new[] { "0", "50", "100", "150", "200" }
                });
            }

            if (CanShow(PID_LOAD))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "loadGauge", new
                {
                    type = "linear",
                    value = carData?.EngineLoadPercent ?? 0,
                    minValue = 0,
                    maxValue = 100,
                    valueSuffix = "%",
                    linearOverrides = new { needleSide = "right" }
                });
            }

            if (CanShow(PID_FPRESS))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "fpGauge", new
                {
                    type = "radial",
                    value = carData?.FuelPressureKpa ?? 0,
                    minValue = 0,
                    maxValue = 700,
                    units = "kPa",
                    majorTicks = new[] { "0", "100", "200", "300", "400", "500", "600", "700" }
                });
            }

            if (CanShow(PID_MAP))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "mapGauge", new
                {
                    type = "radial",
                    value = carData?.MapKpa ?? 0,
                    minValue = 0,
                    maxValue = 255,
                    units = "kPa",
                    majorTicks = new[] { "0", "50", "100", "150", "200", "250" }
                });
            }

            if (CanShow(PID_TADV))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "tadvGauge", new
                {
                    type = "radial",
                    value = carData?.TimingAdvanceDeg ?? 0,
                    minValue = -64,
                    maxValue = 63.5,
                    units = "°",
                    majorTicks = new[] { "-60", "-30", "0", "30", "60" }
                });
            }

            if (CanShow(PID_MAF))
            {
                await gaugeModule.InvokeVoidAsync("initGauge", "mafGauge", new
                {
                    type = "radial",
                    value = carData?.MafGramsPerSec ?? 0,
                    minValue = 0,
                    maxValue = 300,
                    units = "g/s",
                    majorTicks = new[] { "0", "50", "100", "150", "200", "250", "300" }
                });
            }

            gaugeReady = true;
        }

        private async Task ReinitProfileScaledGaugesAsync()
        {
            if (gaugeModule is null) return;

            try { await gaugeModule.InvokeVoidAsync("dispose", "rpmGauge"); } catch { }
            try { await gaugeModule.InvokeVoidAsync("dispose", "speedGauge"); } catch { }

            var tachMax = GetTachMax();
            var redStart = GetRedlineStart();
            var tachTicks = BuildTachMajorTicks(tachMax);

            await gaugeModule.InvokeVoidAsync("initGauge", "rpmGauge", new
            {
                type = "radial",
                value = carData?.RPM ?? 0,
                minValue = 0,
                maxValue = tachMax,
                startAngle = 46,
                ticksAngle = 270,
                units = "rpm",
                majorTicks = tachTicks,
                minorTicks = 5,
                highlights = new[] { new { from = redStart, to = tachMax, color = "rgba(255,0,0,.28)" } },
                noResize = true
            });

            var speedMax = GetSpeedMax();
            var speedTicks = BuildSpeedMajorTicks(speedMax);

            await gaugeModule.InvokeVoidAsync("initGauge", "speedGauge", new
            {
                type = "radial",
                value = carData?.Speed ?? 0,
                minValue = 0,
                maxValue = speedMax,
                units = "mph",
                majorTicks = speedTicks,
                minorTicks = 2,
                highlights = Array.Empty<object>()
            });
        }

        protected override async Task OnParametersSetAsync()
        {
            await RecomputeNeedsSetupAsync();

            if (_needsSetup && ObdService.IsEcuConnected)
            {
                await ObdService.Disconnect();
            }

            var key = ProfileKey(Profiles?.Current);
            if (key != _lastProfileKey)
            {
                _lastProfileKey = key;
                _selectedReplayId = "";
            }

            if (key != _lastProfileKey && gaugeReady)
            {
                _lastProfileKey = key;
                await ReinitProfileScaledGaugesAsync();
                StateHasChanged();
            }
        }

        private async Task SwitchView(DisplayMode v)
        {
            if (_view == v) return;

            var wasGauges = _view == DisplayMode.Gauges;
            _view = v;
            await SaveDashboardPrefsAsync();
            StateHasChanged();

            if (wasGauges && v != DisplayMode.Gauges)
            {
                await DisposeGaugesAsync(all: true);
                gaugeReady = false;
            }

            if (v == DisplayMode.Gauges)
            {
                gaugeReady = false;
                await Task.Yield();
                await EnsureGaugesInitializedAsync();
                if (gaugeModule is not null) await gaugeModule.InvokeVoidAsync("refreshOnShow");
            }
        }

        private async Task SafeSetAllGaugesAsync()
        {
            if (_view != DisplayMode.Gauges || !gaugeReady || gaugeModule is null) return;

            var fast = new { durSmall = 90, durMed = 160, durBig = 260, durMax = 320 };
            var med = new { durSmall = 120, durMed = 220, durBig = 360, durMax = 420 };
            var slow = new { durSmall = 160, durMed = 260, durBig = 420, durMax = 480 };

            async Task SetIf(string id, double? value, object opts)
            {
                try { await gaugeModule.InvokeVoidAsync("setSmooth", id, value ?? 0, opts); } catch { }
            }

            if (CanShow(PID_RPM)) await SetIf("rpmGauge", carData?.RPM, fast);
            if (CanShow(PID_TPS)) await SetIf("throttleGauge", carData?.ThrottlePercent, fast);
            if (CanShow(PID_SPEED)) await SetIf("speedGauge", carData?.Speed, fast);
            if (CanShow(PID_LOAD)) await SetIf("loadGauge", carData?.EngineLoadPercent, fast);
            if (CanShow(PID_IAT)) await SetIf("iatGauge", carData?.IntakeTempF, med);
            if (CanShow(PID_COOLANT)) await SetIf("coolantGauge", carData?.CoolantTempF, med);
            if (CanShow(PID_FPRESS)) await SetIf("fpGauge", carData?.FuelPressureKpa, med);
            if (CanShow(PID_MAP)) await SetIf("mapGauge", carData?.MapKpa, med);
            if (CanShow(PID_TADV)) await SetIf("tadvGauge", carData?.TimingAdvanceDeg, med);
            if (CanShow(PID_FUEL)) await SetIf("fuelGauge", carData?.FuelLevelPercent, slow);
            if (CanShow(PID_OIL)) await SetIf("oilGauge", carData?.OilTempF, slow);
            if (CanShow(PID_MAF)) await SetIf("mafGauge", carData?.MafGramsPerSec, slow);
        }

        private static string FormatInt(int? n) => n is null ? "—" : n.Value.ToString();
        private static string FormatInt(double? n) => n is null ? "—" : Math.Round(n.Value).ToString("0");

        private string RpmClass(double? r)
        {
            if (r is null) return "";
            var red = GetRedlineStart();
            var warn = (int)(red * 0.8);
            return r >= red ? "bad" : r >= warn ? "warn" : "good";
        }
        private static string SpeedClass(double? s) => s is null ? "" : (s >= 120 ? "bad" : s >= 90 ? "warn" : "good");
        private static string ThrottleClass(double? t) => t is null ? "" : (t >= 90 ? "bad" : t >= 70 ? "warn" : "good");
        private static string FuelClass(double? f) => f is null ? "" : (f <= 10 ? "bad" : f <= 25 ? "warn" : "good");
        private static string TempClass(double? f) => f is null ? "" : (f >= 230 ? "bad" : f >= 215 ? "warn" : "good");
        private static string IatClass(double? f) => f is null ? "" : (f >= 150 ? "bad" : f >= 120 ? "warn" : "good");
        private static string LoadClass(double? p)
        {
            if (p is null) return "";
            return p >= 90 ? "bad" : p >= 70 ? "warn" : "good";
        }

        private static string MapClass(double? kpa)
        {
            if (kpa is null) return "";
            return kpa >= 180 ? "bad" : kpa >= 130 ? "warn" : "good";
        }

        private static string TimingClass(double? deg)
        {
            if (deg is null) return "";
            return deg <= -10 ? "bad" : deg <= -2 ? "warn" : "good";
        }


        private async Task SwitchToLive()
        {
            if (_needsSetup) { Nav.NavigateTo("/setup"); return; }

            Log("🔌 Attempting to connect to Live OBD-II...");
            bool connected = await ObdService.AutoConnectAsync();
            if (connected)
            {
                Log("✅ Connected to OBD-II adapter successfully!");
                await ZeroOutDataAsync();

                if (!ObdService.SimulationMode && ObdService.IsEcuConnected)
                {
                    Log("📡 Starting live ECU data stream...");
                    _ = ObdService.StartLiveDataLoopAsync(async cd =>
                    {
                        var newPlan = (ObdService.CurrentPollPlan ?? Array.Empty<string>())
                                      .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool planChanged = !_planKnown || !_plan.SetEquals(newPlan);

                        await InvokeAsync(async () =>
                        {
                            carData = cd;

                            if (planChanged)
                            {
                                _plan = newPlan;
                                gaugeReady = false;
                                StateHasChanged();
                                await Task.Yield();
                                await EnsureGaugesInitializedAsync();
                            }

                            await SafeSetAllGaugesAsync();
                            StateHasChanged();
                        });
                    });
                }
                else
                {
                    Log("⚠️ ECU not responding — check ignition / key-on and try again.");
                }
            }

            await InvokeAsync(StateHasChanged);
        }

        private async Task ReconnectEcu()
        {
            if (await ObdService.TryReconnectEcuAsync())
            {
                Log("✅ ECU is now online!");
                await ZeroOutDataAsync();

                if (!ObdService.SimulationMode && ObdService.IsEcuConnected)
                {
                    Log("📡 Resuming live ECU data stream...");
                    _ = ObdService.StartLiveDataLoopAsync(async cd =>
                    {
                        await InvokeAsync(async () =>
                        {
                            carData = cd;
                            await SafeSetAllGaugesAsync();
                            StateHasChanged();
                        });
                    });
                }
            }
            else
            {
                Log("❌ ECU still offline.");
            }
        }

        private async Task AttachLiveStreamAsync()
        {
            if (_attachInProgress) return;
            _attachInProgress = true;
            try
            {
                if (ObdService.IsEcuConnected && !ObdService.SimulationMode)
                {
                    _plan = (ObdService.CurrentPollPlan ?? Array.Empty<string>())
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    carData = ObdService.LastSnapshot;

                    if (_view == DisplayMode.Gauges)
                    {
                        gaugeReady = false;
                        await EnsureGaugesInitializedAsync();
                        await SafeSetAllGaugesAsync();
                    }

                    _ = ObdService.StartLiveDataLoopAsync(async cd =>
                    {
                        var newPlan = (ObdService.CurrentPollPlan ?? Array.Empty<string>())
                                      .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        bool planChanged = !_planKnown || !_plan.SetEquals(newPlan);

                        await InvokeAsync(async () =>
                        {
                            carData = cd;

                            if (planChanged)
                            {
                                _plan = newPlan;
                                gaugeReady = false;
                                StateHasChanged();
                                await Task.Yield();
                                await EnsureGaugesInitializedAsync();
                            }

                            await SafeSetAllGaugesAsync();
                            StateHasChanged();
                        });
                    });
                }
            }
            finally { _attachInProgress = false; }
        }

        private Task CancelConnecting()
        {
            ObdService.CancelConnection();
            return Task.CompletedTask;
        }

        private async Task Disconnect()
        {
            await ObdService.Disconnect();
            await InvokeAsync(StateHasChanged);
        }

        private void HandleLog(string message)
        {
            outputLog.Add($"[{DateTime.Now:T}] {message}");
            if (outputLog.Count > 500) outputLog.RemoveAt(0);
            InvokeAsync(StateHasChanged);
        }

        private void Log(string message) => HandleLog(message);

        private string GetModeText() =>
            ObdService.IsConnecting
                ? (ObdService.IsCancelRequested ? "Canceling…" : "Connecting…")
                : ObdService.IsEcuConnected
                    ? "Live ECU Connected"
                    : ObdService.IsAdapterConnected
                        ? "Adapter Connected (ECU Offline)"
                        : _isReplaying ? "Replay Mode" : "Disconnected";

        private string GetModeClass() =>
            ObdService.IsConnecting ? "is-connecting" :
            ObdService.IsEcuConnected ? "is-live" :
            ObdService.IsAdapterConnected ? "is-adapter" :
            _isReplaying ? "is-sim" : "is-disc";

        private async Task ZeroOutDataAsync()
        {
            carData ??= new CarData();
            carData.RPM = 0;
            carData.Speed = 0;
            carData.ThrottlePercent = 0;
            carData.FuelLevelPercent = 0;
            carData.CoolantTempF = 0;
            carData.OilTempF = 0;
            carData.IntakeTempF = 0;
            carData.LastUpdated = DateTime.Now;

            await SafeSetAllGaugesAsync();
            await InvokeAsync(StateHasChanged);
        }

        public async ValueTask DisposeAsync()
        {
            if (gaugeModule is not null)
            {
                try { await gaugeModule.InvokeVoidAsync("disposeAll"); } catch { }
                await gaugeModule.DisposeAsync();
            }

            if (downloadModule is not null)
            {
                try { await downloadModule.DisposeAsync(); } catch { }
            }

            _replayTimer?.Stop();
            _replayTimer?.Dispose();

            ObdService.OnLog -= HandleLog;
            ObdService.OnData -= HandleLiveData;
            if (_fpHandler is not null) ObdService.OnFingerprint -= _fpHandler;
            if (_stateHandler is not null) ObdService.OnStateChanged -= _stateHandler;

            ObdService.OnReplayCompleted -= HandleReplayCompleted;

            Replayer.Stop();
            if (_isRecording) try { await Recorder.StopAsync(); } catch { }
        }

        private static readonly string[] GaugeIds = new[]
        {
            "rpmGauge","speedGauge","throttleGauge","fuelGauge","coolantGauge",
            "oilGauge","iatGauge","loadGauge","fpGauge","mapGauge","tadvGauge","mafGauge"
        };

        private async Task DisposeGaugesAsync(bool all = false)
        {
            if (gaugeModule is null) return;
            try
            {
                if (all)
                {
                    await gaugeModule.InvokeVoidAsync("disposeAll");
                }
                else
                {
                    foreach (var id in GaugeIds)
                    {
                        try { await gaugeModule.InvokeVoidAsync("dispose", id); } catch { }
                    }
                }
            }
            catch { }
        }

        private async Task DebugDumpSelectedRecordingAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedReplayId))
            {
                Log("[DEBUG] No recording selected to dump.");
                return;
            }

            var text = await Recorder.DumpRecordingAsync(_selectedReplayId);
            if (text is null)
            {
                Log($"[DEBUG] No payload found for recording '{_selectedReplayId}'.");
                return;
            }

            var lines = text.Split('\n')
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .Take(20);

            foreach (var line in lines)
            {
                Log($"[REC DEBUG] {line}");
            }
        }

        public void ClearLog()
        {
            outputLog.Clear();
            ObdService.ClearLog();
            StateHasChanged();
        }

        private static string FormatMs(long? ms)
        {
            if (ms is null || ms <= 0) return "00:00";
            var ts = TimeSpan.FromMilliseconds(ms.Value);
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";
        }

        private async Task ExportSelectedRecordingAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                Log("⚠️ No recording selected to export.");
                return;
            }

            _selectedReplayId = id;

            var text = await Recorder.DumpRecordingAsync(id);
            if (text is null)
            {
                Log($"❌ No payload found for recording '{id}'.");
                return;
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 1)
            {
                Log($"❌ Recording '{id}' has no data frames.");
                return;
            }

            var frames = new List<CarRecordingFrame>();
            for (int i = 1; i < lines.Length; i++)
            {
                try
                {
                    var frame = JsonSerializer.Deserialize<CarRecordingFrame>(lines[i], RecJsonOpts);
                    if (frame is not null)
                        frames.Add(frame);
                }
                catch
                {
                    // ignore bad lines
                }
            }

            if (frames.Count == 0)
            {
                Log($"❌ Could not parse any frames for recording '{id}'.");
                return;
            }

            static bool HasNumericValue(object? v) => v switch
            {
                null => false,
                int i => i != 0,
                long l => l != 0,
                short s => s != 0,
                byte b => b != 0,
                float f => Math.Abs(f) > float.Epsilon,
                double d => Math.Abs(d) > double.Epsilon,
                decimal m => m != 0m,
                _ => false
            };

            bool rpmHasData = frames.Any(f => HasNumericValue(f.data?.RPM));
            bool speedHasData = frames.Any(f => HasNumericValue(f.data?.Speed));
            bool tpsHasData = frames.Any(f => HasNumericValue(f.data?.ThrottlePercent));
            bool fuelHasData = frames.Any(f => HasNumericValue(f.data?.FuelLevelPercent));
            bool coolantHasData = frames.Any(f => HasNumericValue(f.data?.CoolantTempF));
            bool oilHasData = frames.Any(f => HasNumericValue(f.data?.OilTempF));
            bool iatHasData = frames.Any(f => HasNumericValue(f.data?.IntakeTempF));
            bool loadHasData = frames.Any(f => HasNumericValue(f.data?.EngineLoadPercent));
            bool mapHasData = frames.Any(f => HasNumericValue(f.data?.MapKpa));
            bool mafHasData = frames.Any(f => HasNumericValue(f.data?.MafGramsPerSec));

            string C(object? v) =>
                v switch
                {
                    null => "",
                    IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString() ?? ""
                };

            var sb = new StringBuilder();

            var headers = new List<string> { "time_ms" };
            if (rpmHasData) headers.Add("rpm");
            if (speedHasData) headers.Add("speed_mph");
            if (tpsHasData) headers.Add("throttle_pct");
            if (fuelHasData) headers.Add("fuel_pct");
            if (coolantHasData) headers.Add("coolant_f");
            if (oilHasData) headers.Add("oil_f");
            if (iatHasData) headers.Add("iat_f");
            if (loadHasData) headers.Add("load_pct");
            if (mapHasData) headers.Add("map_kpa");
            if (mafHasData) headers.Add("maf_gps");

            sb.AppendLine(string.Join(',', headers));

            foreach (var f in frames)
            {
                var d = f.data ?? new CarData();
                var row = new List<string> { C(f.t) };

                if (rpmHasData) row.Add(C(d.RPM));
                if (speedHasData) row.Add(C(d.Speed));
                if (tpsHasData) row.Add(C(d.ThrottlePercent));
                if (fuelHasData) row.Add(C(d.FuelLevelPercent));
                if (coolantHasData) row.Add(C(d.CoolantTempF));
                if (oilHasData) row.Add(C(d.OilTempF));
                if (iatHasData) row.Add(C(d.IntakeTempF));
                if (loadHasData) row.Add(C(d.EngineLoadPercent));
                if (mapHasData) row.Add(C(d.MapKpa));
                if (mafHasData) row.Add(C(d.MafGramsPerSec));

                sb.AppendLine(string.Join(',', row));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var base64 = Convert.ToBase64String(bytes);
            var filename = $"carspec-recording-{id}.csv";

            downloadModule ??= await JS.InvokeAsync<IJSObjectReference>(
                "import", "./js/carspec.downloads.js");

            await downloadModule.InvokeVoidAsync(
                "downloadBytes",
                filename,
                "text/csv;charset=utf-8",
                base64
            );

            Log($"⬇️ Exported recording '{id}' as CSV.");
        }

        private static readonly JsonSerializerOptions RecJsonOpts = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }
}