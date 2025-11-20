using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using CarSpec.Models;
using CarSpec.Services.Telemetry;
using CarSpec.Services.Obd;
using CarSpec.Interfaces;
using CarSpec.Utils;
using CarSpec.Constants;

namespace CarSpec.Components.Pages.Dashboard
{
    public partial class Dashboard : ComponentBase, IAsyncDisposable
    {
        // Injects
        [Inject] public ObdConnectionService ObdService { get; set; } = default!;
        [Inject] public IJSRuntime JS { get; set; } = default!;
        [Inject] public IVehicleProfileService Profiles { get; set; } = default!;
        [Inject] public NavigationManager Nav { get; set; } = default!;
        [Inject] public IAppStorage Storage { get; set; } = default!;
        [Inject] public RecordingService Recorder { get; set; } = default!;
        [Inject] public ReplayService Replayer { get; set; } = default!;

        private CarData? carData;
        private readonly List<string> outputLog = new();
        private CancellationTokenSource? _simCts;
        private readonly TimeSpan _simTick = TimeSpan.FromSeconds(1);
        private enum DisplayMode { Gauges, Numbers }
        private DisplayMode _view = DisplayMode.Gauges;
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
        private bool IsLive => ObdService.IsEcuConnected && !ObdService.SimulationMode;
        private bool ShouldRenderGauges => _isReplaying || (IsLive && _planKnown);
        private bool _isPreparingReplay;

        private IJSObjectReference? gaugeModule;
        private bool gaugeReady;

        private void RunSetup() => Nav.NavigateTo("/setup");

        // --- PID-aware UI state ---
        private HashSet<string> _plan = new(StringComparer.OrdinalIgnoreCase);
        private bool _planKnown => _plan.Count > 0;
        private bool CanShow(string pid)
        {
            if (_isReplaying) return true;
            return _plan.Contains(pid);
        }

        // PID constants
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

            Log("🚀 CarSpec Dashboard Successfully Started...");

            await RecomputeNeedsSetupAsync();
            _lastProfileKey = ProfileKey(Profiles?.Current);

            try { carData = ObdService.LastSnapshot ?? await ObdService.GetLatestDataAsync(); } catch { }
            try { _replays = await Recorder.ListAsync(); } catch { _replays = new(); }

            if (!_needsSetup && ObdService.IsEcuConnected && !ObdService.SimulationMode)
                await AttachLiveStreamAsync();
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

        private async void HandleLiveData(CarData cd)
        {
            if (_isRecording)
            {
                try { await Recorder.AppendAsync(cd); } catch { }
            }

            var newPlan = (ObdService.CurrentPollPlan ?? Array.Empty<string>())
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool planChanged = !_planKnown || !_plan.SetEquals(newPlan);

            await InvokeAsync(async () =>
            {
                carData = cd;

                if (planChanged)
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

            if (string.IsNullOrWhiteSpace(_selectedReplayId) || _isPreparingReplay) return;

            _isPreparingReplay = true;
            _isReplayPaused = false;   // 👈 ensure we're not "paused" at start
            StateHasChanged();

            if (_isRecording) await StopRecording();
            await ObdService.Disconnect();
            ObdService.SimulationMode = true;

            _isReplaying = true;
            StateHasChanged();

            if (_view == DisplayMode.Gauges)
            {
                gaugeReady = false;
                await DisposeGaugesAsync(all: true);
                await ZeroOutDataAsync();
                StateHasChanged();
                await Task.Yield();
                await EnsureGaugesInitializedAsync();
                await Task.Delay(16);
            }

            _isPreparingReplay = false;
            StateHasChanged();

            double speed = _replaySpeed <= 0 ? 1.0 : _replaySpeed;
            Log($"▶️ Starting replay {_selectedReplayId} at {speed:0.##}×");

            await Replayer.StartAsync(
                _selectedReplayId,
                onFrame: async cd =>
                {
                    await InvokeAsync(async () =>
                    {
                        carData = cd;
                        await SafeSetAllGaugesAsync();
                        StateHasChanged();
                    });
                },
                onStop: () =>
                {
                    _isReplaying = false;
                    _isReplayPaused = false;      // 👈 reset here too
                    _ = InvokeAsync(async () =>
                    {
                        await DisposeGaugesAsync(all: true);
                        gaugeReady = false;
                        StateHasChanged();
                    });
                    Log("⏹️ Replay finished.");
                },
                speed: speed
            );
        }

        private Task StopReplay()
        {
            if (!_isReplaying) return Task.CompletedTask;
            Replayer.Stop();
            _isReplaying = false;
            _isReplayPaused = false;   // 👈 reset
            Log("⏹️ Replay stopped.");
            return Task.CompletedTask;
        }

        private Task PauseReplay()
        {
            if (!_isReplaying || _isReplayPaused) return Task.CompletedTask;

            Replayer.Pause();          // 👈 we'll add this to ReplayService
            _isReplayPaused = true;
            Log("⏸ Replay paused.");
            StateHasChanged();
            return Task.CompletedTask;
        }

        private Task ResumeReplay()
        {
            if (!_isReplaying || !_isReplayPaused) return Task.CompletedTask;

            Replayer.Resume();         // 👈 and this
            _isReplayPaused = false;
            Log("▶ Replay resumed.");
            StateHasChanged();
            return Task.CompletedTask;
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
                ObdService.SimulationMode = true;
            }

            var key = ProfileKey(Profiles?.Current);
            if (key != _lastProfileKey && gaugeReady)
            {
                _lastProfileKey = key;
                await ReinitProfileScaledGaugesAsync();
                StateHasChanged();
            }
        }

        private async Task SwitchView(DisplayMode v)
        {
            var wasGauges = _view == DisplayMode.Gauges;
            _view = v;
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

        private void StartSimulationLoop()
        {
            _simCts?.Cancel();
            _simCts = new CancellationTokenSource();
            var token = _simCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (ObdService.SimulationMode)
                        {
                            carData = CarData.Simulated();
                            await InvokeAsync(async () =>
                            {
                                await SafeSetAllGaugesAsync();
                                StateHasChanged();
                            });
                        }
                    }
                    catch { }
                    try { await Task.Delay(_simTick, token); } catch { }
                }
            }, token);
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
                    Log("⚠️ ECU not responding — staying in Simulation Mode.");
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
            // ObdService.SimulationMode = true;
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
                : (ObdService.IsEcuConnected ? "Live ECU Connected"
                   : (ObdService.IsAdapterConnected ? "Adapter Connected (ECU Offline)"
                      : ObdService.SimulationMode ? "Simulation Mode" : "Disconnected"));

        private string GetModeClass() =>
            ObdService.IsConnecting ? "is-connecting" :
            ObdService.IsEcuConnected ? "is-live" :
            (ObdService.IsAdapterConnected ? "is-adapter" :
            ObdService.SimulationMode ? "is-sim" : "is-disc");

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
            _simCts?.Cancel();
            _simCts = null;

            if (gaugeModule is not null)
            {
                try { await gaugeModule.InvokeVoidAsync("disposeAll"); } catch { }
                await gaugeModule.DisposeAsync();
            }

            ObdService.OnLog -= HandleLog;
            ObdService.OnData -= HandleLiveData;
            if (_fpHandler is not null) ObdService.OnFingerprint -= _fpHandler;
            if (_stateHandler is not null) ObdService.OnStateChanged -= _stateHandler;

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
                        try { await gaugeModule.InvokeVoidAsync("dispose", id); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
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
                            .Take(20); // don’t spam too hard

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
    }
}