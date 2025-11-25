using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;
using CarSpec.Utils.OBDData;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// High-level orchestrator that discovers an ELM327 adapter, connects,
    /// wakes ISO/KWP ECUs when needed, enforces profile match, persists learned
    /// details (VIN/WMI/Year/Protocol/SupportedPIDs), and runs a live polling loop.
    /// </summary>
    public class ObdConnectionService
    {
        private readonly BluetoothManager _bluetooth;
        private readonly IVehicleProfileService _profiles;
        private Elm327Adapter? _adapter;
        private ObdService? _obdService;
        private readonly Logger _log = new("OBD-CONNECT");
        private CancellationTokenSource? _liveCts;
        private CancellationTokenSource? _connectCts;
        private readonly Telemetry.RecordingService _rec;
        private readonly Telemetry.ReplayService _replay;

        private readonly ObdDataRegistry _registry = ObdDataRegistry.Default;

        private HashSet<string> _supportedPids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _noDataStrikes = new(StringComparer.OrdinalIgnoreCase);
        private const int NoDataStrikeLimit = 3;

        public enum ProfileMatchResult
        {
            Match,
            SoftUnknown,
            SoftMismatch,
            HardMismatch
        }

        public VehicleProfile? CurrentProfile => _profiles.Current;

        public bool SimulationMode { get; set; } = true;

        public bool IsConnected => _adapter?.IsConnected ?? false;
        public bool IsConnecting { get; private set; }
        public bool IsCancelRequested => _connectCts?.IsCancellationRequested == true;
        public bool IsAdapterConnected { get; private set; }
        public bool IsEcuConnected { get; private set; }

        private bool _isIso;
        private int _pidTimeoutMs = 600;

        private readonly object _logLock = new();
        private readonly Queue<string> _logQ = new();
        private const int MaxLogLines = 500;
        private bool _loopRunning;

        public CarData? LastSnapshot { get; private set; }
        private EcuFingerprint? _lastFp;
        public EcuFingerprint? LastFingerprint => _adapter?.LastFingerprint;

        public event Action<EcuFingerprint?>? OnFingerprint;
        public event Action<string>? OnLog;
        public event Action<CarData>? OnData;

        public event Action? OnStateChanged;
        private void Notify() => OnStateChanged?.Invoke();

        /// <summary>For telemetry/UX: last computed poll plan.</summary>
        public IReadOnlyList<string> CurrentPollPlan { get; private set; } = new List<string>();

        public IReadOnlyList<string> GetLogSnapshot()
        {
            lock (_logLock) return _logQ.ToList();
        }

        public void ClearLog()
        {
            lock (_logLock)
            {
                _logQ.Clear();
            }
        }

        public ObdConnectionService(
            IVehicleProfileService profiles,
            Telemetry.RecordingService rec,
            Telemetry.ReplayService replay)
        {
            _profiles = profiles;
            _rec = rec;
            _replay = replay;
            _bluetooth = new BluetoothManager();
            _bluetooth.OnLog += Log;
        }

        // ---------- Public connect/disconnect ----------

        public async Task<bool> AutoConnectAsync()
        {
            if (!await EnsureAdapterAsync())
                return false;

            return await ConnectAsync();
        }

        public void CancelConnection()
        {
            if (!IsConnecting) return;
            _connectCts?.Cancel();
            Log("🛑 Connection attempt cancelled by user.");
        }

        private void TearDownAfterCancel()
        {
            try { _adapter?.Disconnect(); } catch { /* ignore */ }
            _adapter = null;
            _obdService = null;
            IsAdapterConnected = false;
            IsEcuConnected = false;
            SimulationMode = true;
            Notify();
        }

        public async Task<bool> ConnectAsync()
        {
            if (IsConnecting) return false;

            IsConnecting = true;
            Notify();

            _connectCts?.Cancel();
            _connectCts = new CancellationTokenSource();
            var ct = _connectCts.Token;

            try
            {
                return await ConnectCoreAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Log("⛔ Connect canceled.");
                TearDownAfterCancel();
                return false;
            }
            catch (Exception ex)
            {
                Log($"❌ Connect exception: {ex.Message}");
                TearDownAfterCancel();
                return false;
            }
            finally
            {
                IsConnecting = false;
                try { _connectCts?.Dispose(); } catch { /* ignore */ }
                _connectCts = null;
                Notify();
            }
        }

        private async Task<bool> ConnectCoreAsync(CancellationToken ct)
        {
            try
            {
                Log("🔌 Starting ELM327 connection...");

                ct.ThrowIfCancellationRequested();

                if (!await EnsureAdapterAsync()) return false;

                ct.ThrowIfCancellationRequested();

                var ok = await _adapter!.ConnectAsync(ct);

                IsAdapterConnected = _adapter.IsConnected;
                IsEcuConnected = _adapter.IsEcuAwake;
                Notify();

                await ApplyProfileProtocolHintAsync();

                ct.ThrowIfCancellationRequested();

                if (!ok || !_adapter.IsConnected)
                {
                    Log("❌ Adapter connect failed.");
                    SimulationMode = true;
                    _obdService = null;
                    Notify();
                    return false;
                }

                if (!_adapter.IsEcuAwake)
                {
                    Log("⚠️ ECU not responding — attempting ISO/KWP wake...");
                    var woke = await TryWakeEcuAsync(ct);
                    IsEcuConnected = woke;
                    Notify();

                    if (!woke)
                    {
                        Log("⚠️ ECU still not responding — staying non-live (no ECU data).");
                        SimulationMode = true;
                        _obdService = null;
                        Notify();
                        return true;
                    }
                }

                OnFingerprint?.Invoke(_adapter.LastFingerprint);

                _obdService = new ObdService(_adapter);
                SimulationMode = false;
                Log("✅ Live OBD connected.");
                Notify();

                ct.ThrowIfCancellationRequested();

                _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync(ct);
                if (string.IsNullOrWhiteSpace(_lastFp?.Vin))
                {
                    await Task.Delay(250, ct);
                    _lastFp = await _adapter.ReadFingerprintAsync(ct);
                }
                OnFingerprint?.Invoke(_lastFp);

                await _profiles.LoadAsync();
                var curProfile = _profiles.Current;
                if (curProfile != null && _lastFp != null)
                {
                    var match = ValidateProfileMatch(curProfile, _lastFp);
                    bool strict = curProfile.StrictProfileLock;

                    if (match == ProfileMatchResult.HardMismatch ||
                        (strict && match != ProfileMatchResult.Match))
                    {
                        Log($"⛔ Profile '{curProfile.Year} {curProfile.Make} {curProfile.Model}' " +
                            $"does not match connected vehicle (VIN={_lastFp.Vin ?? "unknown"}, " +
                            $"WMI={_lastFp.Wmi ?? "?"}, Protocol={_lastFp.Protocol ?? "?"}). Aborting.");
                        await Disconnect();
                        SimulationMode = true;
                        Notify();
                        return false;
                    }

                    if (match == ProfileMatchResult.SoftMismatch)
                        Log("⚠️ Connected vehicle doesn’t strongly match the selected profile. Proceeding.");
                }

                await _profiles.LoadAsync();
                var cur = _profiles.Current;

                var supportedNow = _adapter.GetSupportedPids()?.ToList() ?? new List<string>();
                var newProtoRaw = _lastFp?.Protocol;
                var newProto = NormalizeProtocol(newProtoRaw);
                var oldProto = NormalizeProtocol(cur?.ProtocolDetected);
                var oldSupported = cur?.SupportedPidsCache ?? new List<string>();

                bool isBrandNew =
                    cur is null ||
                    cur.LastConnectedUtc == default ||
                    string.IsNullOrWhiteSpace(cur.ProtocolDetected) ||
                    (cur.SupportedPidsCache is null || cur.SupportedPidsCache.Count == 0);

                bool protoChanged = !string.Equals(newProto ?? "", oldProto ?? "", StringComparison.OrdinalIgnoreCase);
                bool pidsChanged = !SameSet(supportedNow, oldSupported);

                bool hasNewIdentityBits =
                    !string.IsNullOrWhiteSpace(_lastFp?.Vin) ||
                    !string.IsNullOrWhiteSpace(_lastFp?.Wmi) ||
                    (_lastFp?.Year.HasValue ?? false);

                var lastConnectedStr = (cur?.LastConnectedUtc.HasValue == true)
                    ? cur!.LastConnectedUtc!.Value.ToString("u")
                    : "∅";

                Log($"🔎 Pre-save check: oldProto='{cur?.ProtocolDetected ?? "∅"}', newProto='{newProtoRaw ?? "∅"}' (norm='{newProto ?? "∅"}'), " +
                    $"oldPIDCount={oldSupported.Count}, newPIDCount={supportedNow.Count}, lastConnected={lastConnectedStr}");

                bool needsPersist = isBrandNew || protoChanged || pidsChanged || hasNewIdentityBits;

                if (needsPersist)
                {
                    if (cur is not null && !string.IsNullOrWhiteSpace(newProto) &&
                        !string.Equals(cur.ProtocolDetected ?? "", newProto, StringComparison.OrdinalIgnoreCase))
                    {
                        cur.ProtocolDetected = newProto;
                        await _profiles.SetCurrentAsync(cur);
                        Log("🧹 Normalized stored protocol to concrete value.");
                    }

                    if (_lastFp is not null)
                        await PersistLearnedToCurrentProfileAsync(_lastFp, supportedNow);
                    else
                        Log("ℹ️ No fingerprint available to persist.");
                }
                else
                {
                    Log("ℹ️ Profile unchanged (protocol/PIDs/VIN); skipping save.");
                }

                Notify();

                if (_adapter is not null)
                {
                    _pidTimeoutMs = _adapter.CurrentPidTimeoutMs;
                    _isIso = _adapter.IsIsoProtocol;
                }
                else
                {
                    var p = (_lastFp?.Protocol ?? _profiles.Current?.ProtocolDetected ?? string.Empty).ToUpperInvariant();
                    bool isKLine = p.Contains("9141") || p.Contains("14230") || p.Contains("KWP");
                    bool isCan = p.Contains("15765") || p.Contains("CAN");
                    _isIso = isKLine && !isCan;
                    _pidTimeoutMs = _isIso ? 1500 : 500;
                }

                Log(_isIso ? "🛠 Using ISO-safe cadence." : "🛠 Using CAN-fast cadence.");

                await EnsureLiveLoopRunningAsync(_ => Notify());

                return true;
            }
            catch (OperationCanceledException)
            {
                Log("⛔ Connect canceled during connect/fingerprint.");
                TearDownAfterCancel();
                return false;
            }
            catch (Exception ex)
            {
                Log($"❌ Connect exception: {ex.Message}");
                _obdService = null;
                SimulationMode = true;
                Notify();
                return false;
            }
        }

        public async Task<bool> StartRecordingAsync(string? note = null)
        {
            await _profiles.LoadAsync();
            await _rec.StartAsync(_profiles.Current, _adapter?.LastFingerprint, note);
            Log("⏺️ Recording started.");
            return true;
        }

        public async Task<RecordingMeta?> StopRecordingAsync()
        {
            var meta = await _rec.StopAsync(gzip: true);
            Log($"⏹️ Recording saved ({meta?.Frames} frames, {(meta?.ByteSize ?? 0) / 1024} KB).");
            return meta;
        }

        public async Task<bool> StartReplayAsync(string recordingId, double speed = 1.0)
        {
            SimulationMode = true;
            CancelLiveLoop();
            Notify();

            await _replay.StartAsync(
                recordingId,
                async cd =>
                {
                    LastSnapshot = cd;
                    OnData?.Invoke(cd);
                    Notify();
                    await Task.CompletedTask;
                },
                onStop: () =>
                {
                    Log("⏹️ Replay finished");
                    Notify();
                },
                speed: speed);

            Log($"▶️ Replay started (id={recordingId}, {speed}x).");
            return true;
        }

        public void StopReplay()
        {
            _replay.Stop();
            Log("⏹️ Replay stopped.");
        }

        /// <summary>
        /// Try harder to wake a sleepy ISO/K-line ECU before we bail out.
        /// Returns true if ECU replies to 0100.
        /// </summary>
        private async Task<bool> TryWakeEcuAsync(CancellationToken ct)
        {
            if (_adapter == null) return false;

            await Task.Delay(200, ct);

            try
            {
                await _profiles.LoadAsync();
                var protoRaw = _profiles.Current?.ProtocolDetected;
                var proto = NormalizeProtocol(protoRaw);
                var pref = MapProtoToAtsp(proto);
                if (!string.IsNullOrWhiteSpace(pref))
                {
                    await _adapter.SendCommandAsync(pref);
                    await Task.Delay(150, ct);
                    if (await ProbeAsync(timeoutMs: 1800, attempts: 2, note: pref))
                        return true;
                }
            }
            catch { /* ignore */ }

            async Task<bool> ProbeAsync(int timeoutMs, int attempts, string note)
            {
                for (int i = 1; i <= attempts; i++)
                {
                    var r = await _adapter.SendCommandAsync("0100", timeoutMs: timeoutMs, retryStoppedOnce: true);
                    var raw = (r.RawResponse ?? string.Empty).ToUpperInvariant();
                    Log($"🔎 Wake probe [{note}] #{i}: {(string.IsNullOrWhiteSpace(raw) ? "—" : raw)}");
                    if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("NO DATA") && !raw.Contains("?"))
                        return true;

                    await Task.Delay(timeoutMs / 3, ct);
                }
                return false;
            }

            if (await ProbeAsync(timeoutMs: 1600, attempts: 2, note: "AUTO"))
                return true;

            try
            {
                await _adapter.SendCommandAsync("ATSP3");
                await Task.Delay(150, ct);
                if (await ProbeAsync(timeoutMs: 1800, attempts: 2, note: "SP3"))
                    return true;
            }
            catch { /* ignore */ }

            try
            {
                await _adapter.SendCommandAsync("ATSP4");
                await Task.Delay(150, ct);
                if (await ProbeAsync(timeoutMs: 1800, attempts: 2, note: "SP4"))
                    return true;
            }
            catch { /* ignore */ }

            try
            {
                await _adapter.SendCommandAsync("ATSP5");
                await Task.Delay(150, ct);
                if (await ProbeAsync(timeoutMs: 1800, attempts: 2, note: "SP5"))
                    return true;
            }
            catch { /* ignore */ }

            try { await _adapter.SendCommandAsync("ATSP0"); } catch { /* ignore */ }

            return false;
        }

        private static ProfileMatchResult ValidateProfileMatch(VehicleProfile profile, EcuFingerprint fp)
        {
            var profVin = !string.IsNullOrWhiteSpace(profile.VinLast) ? profile.VinLast : profile.LastKnownVin;
            if (!string.IsNullOrWhiteSpace(profVin) && !string.IsNullOrWhiteSpace(fp.Vin))
            {
                if (!profVin.Equals(fp.Vin, StringComparison.OrdinalIgnoreCase))
                    return ProfileMatchResult.HardMismatch;
            }

            if (string.IsNullOrWhiteSpace(fp.Vin))
                return ProfileMatchResult.Match;

            if (!string.IsNullOrWhiteSpace(profile.WmiLast) && !string.IsNullOrWhiteSpace(fp.Wmi))
            {
                if (!profile.WmiLast.Equals(fp.Wmi, StringComparison.OrdinalIgnoreCase))
                    return ProfileMatchResult.HardMismatch;
            }

            if (profile.YearDetected is int py && fp.Year is int fy)
            {
                if (Math.Abs(py - fy) > 2) return ProfileMatchResult.SoftMismatch;
            }

            return ProfileMatchResult.Match;
        }

        private async Task PersistLearnedToCurrentProfileAsync(EcuFingerprint fp, IReadOnlyCollection<string> supportedNow)
        {
            await _profiles.LoadAsync();
            var cur = _profiles.Current;

            Log($"🧭 Persist check: current profile is {(cur is null ? "null" : $"{cur.Year} {cur.Make} {cur.Model} (Id={cur.Id ?? "—"})")}.");
            Log($"🧭 Fingerprint: VIN={(fp.Vin ?? "—")}, WMI={(fp.Wmi ?? "—")}, Year={(fp.Year?.ToString() ?? "—")}, Protocol={(fp.Protocol ?? "—")}, PIDs={supportedNow?.Count ?? 0}");

            if (cur is null)
            {
                Log("⚠️ No active profile; skipping persistence.");
                return;
            }

            var profileKey = cur.Id ?? VehicleKey(cur);

            var resolvedProto = NormalizeProtocol(fp.Protocol) ?? NormalizeProtocol(cur.ProtocolDetected);

            bool changed = false;

            if (!string.IsNullOrWhiteSpace(fp.Vin) && string.IsNullOrWhiteSpace(cur.VinLast))
            {
                cur.VinLast = fp.Vin;
                cur.LastKnownVin = fp.Vin;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(fp.Wmi) && string.IsNullOrWhiteSpace(cur.WmiLast))
            {
                cur.WmiLast = fp.Wmi;
                changed = true;
            }

            if (fp.Year is int y && cur.YearDetected is null)
            {
                cur.YearDetected = y;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(resolvedProto) &&
                !string.Equals(cur.ProtocolDetected ?? "", resolvedProto, StringComparison.OrdinalIgnoreCase))
            {
                cur.ProtocolDetected = resolvedProto;
                changed = true;
            }

            var newPidList = (supportedNow ?? Array.Empty<string>()).ToList();
            if (!SameSet(newPidList, cur.SupportedPidsCache ?? new List<string>()))
            {
                cur.SupportedPidsCache = newPidList;
                changed = true;
            }

            if (changed)
            {
                cur.LastConnectedUtc = DateTime.UtcNow;

                await _profiles.SetCurrentAsync(cur);
                await _profiles.UpsertAsync(cur);

                Log($"🗂 Saved to profile (Id={profileKey}): " +
                    $"VIN={(cur.VinLast ?? "—")}, WMI={(cur.WmiLast ?? "—")}, Year={(cur.YearDetected?.ToString() ?? "—")}, " +
                    $"Protocol={(cur.ProtocolDetected ?? "—")}, PIDs={(cur.SupportedPidsCache?.Count ?? 0)}");
            }
            else
            {
                Log($"ℹ️ No new learned data for profile (Id={profileKey}); skipping save.");
            }
        }

        public Task Disconnect()
        {
            try { _connectCts?.Cancel(); } catch { /* ignore */ }
            CancelLiveLoop();

            if (_adapter != null)
            {
                try
                {
                    if (_adapter.IsConnected)
                    {
                        _adapter.Disconnect();
                        Log("🔌 Disconnected from ELM327 adapter.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Error during disconnect: {ex.Message}");
                }

                _adapter = null;
            }

            IsAdapterConnected = false;
            IsEcuConnected = false;
            SimulationMode = true;

            Log("🧹 Disconnection complete, back to idle (no live ECU).");
            Notify();
            return Task.CompletedTask;
        }

        public async Task<bool> TryReconnectEcuAsync()
        {
            if (_adapter == null)
            {
                Log("⚠️ No adapter connected — cannot reconnect ECU.");
                return false;
            }

            Log("🔁 Attempting ECU reconnection...");
            var resp = await _adapter.SendCommandAsync("0100");
            var raw = (resp.RawResponse ?? string.Empty).ToUpperInvariant();

            if (!string.IsNullOrWhiteSpace(raw) && !raw.Contains("NO_DATA"))
            {
                Log("✅ ECU reconnected successfully!");
                IsEcuConnected = true;
                SimulationMode = false;
                Notify();

                if (_obdService == null)
                    _obdService = new ObdService(_adapter);

                try
                {
                    _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync();
                    OnFingerprint?.Invoke(_lastFp);

                    await _profiles.LoadAsync();
                    var curProfile = _profiles.Current;
                    if (curProfile != null && _lastFp != null)
                    {
                        var match = ValidateProfileMatch(curProfile, _lastFp);
                        if (match == ProfileMatchResult.HardMismatch ||
                            (curProfile.StrictProfileLock && match != ProfileMatchResult.Match))
                        {
                            Log("⛔ Reconnected ECU does not match the active profile. Disconnecting.");
                            await Disconnect();
                            return false;
                        }
                    }

                    if (curProfile != null)
                    {
                        var resolvedProto = NormalizeProtocol(_lastFp?.Protocol)
                                            ?? NormalizeProtocol(curProfile.ProtocolDetected);
                        if (!string.IsNullOrWhiteSpace(resolvedProto))
                        {
                            curProfile.ProtocolDetected = resolvedProto;
                            await _profiles.SetCurrentAsync(curProfile);
                            Notify();
                        }
                    }

                    if (_lastFp?.Vin != null)
                        Log($"🪪 VIN: {_lastFp.Vin} (Year≈{_lastFp.Year?.ToString() ?? "?"}, Protocol={_lastFp.Protocol})");

                    await _profiles.LearnFromFingerprintAsync(_lastFp!, transport: "BLE");

                    if (_adapter is not null)
                    {
                        _pidTimeoutMs = _adapter.CurrentPidTimeoutMs;
                        _isIso = _adapter.IsIsoProtocol;
                    }
                    else
                    {
                        var p = (_lastFp?.Protocol ?? _profiles.Current?.ProtocolDetected ?? string.Empty)
                                .ToUpperInvariant();

                        bool isKLine = p.Contains("9141") || p.Contains("14230") || p.Contains("KWP");
                        bool isCan = p.Contains("15765") || p.Contains("CAN");
                        _isIso = isKLine && !isCan;

                        _pidTimeoutMs = _isIso ? 1500 : 500;
                    }

                    await EnsureLiveLoopRunningAsync(_ => Notify());
                }
                catch (Exception ex)
                {
                    Log($"ℹ️ Fingerprint caching skipped: {ex.Message}");
                }

                return true;
            }

            Log("⚠️ ECU still not responding — car may be off.");
            return false;
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            if (_obdService != null && !SimulationMode)
                return await _obdService.GetLatestDataAsync();

            if (LastSnapshot != null)
                return LastSnapshot;

            return new CarData { LastUpdated = DateTime.Now };
        }

            return new CarData { LastUpdated = DateTime.Now };
        }

        private async Task EnsureCapabilitiesAsync()
        {
            await _profiles.LoadAsync();

            if (_adapter is not null && _adapter.IsConnected)
            {
                var live = _adapter.GetSupportedPids();
                if (live.Count > 0)
                {
                    _supportedPids = live.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Log($"🧭 ECU supports: {string.Join(", ", _supportedPids.OrderBy(s => s))}");
                    return;
                }
            }

            if (_supportedPids.Count == 0)
                Log("ℹ️ No supported PID map available yet; will probe optimistically.");
        }

        public IReadOnlySet<string> GetSupportedPids() => _supportedPids;

        // ---------- Live polling using registry ----------

        public async Task StartLiveDataLoopAsync(Action<CarData>? onData = null)
        {
            if (_loopRunning) return;

            CancelLiveLoop();
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;

            if (_adapter == null || !_adapter.IsConnected || SimulationMode)
            {
                Log("⚠️ Cannot start live data loop — adapter not connected or not in live ECU mode.");
                _liveCts = null;
                return;
            }

            _pidTimeoutMs = _adapter.CurrentPidTimeoutMs;
            var isIso = _adapter.IsIsoProtocol;

            await _profiles.LoadAsync();
            await EnsureCapabilitiesAsync();
            var profile = _profiles.Current;

            var supported = (_supportedPids.Count > 0)
                ? _supportedPids
                : (profile?.SupportedPidsCache?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var desired = (profile?.DesiredMode01Pids ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (desired.Count == 0)
                desired = new()
                {
                    "010C","0111","010D","0105","010F","012F","015C","0104","010A",
                    "010B","010E","0110","0121","0122","0123","012C","012D","012E",
                    "0130","0131","0132","0133"
                };

            desired = desired.Where(pid => _registry.TryCreate(pid, out _)).ToList();

            if (supported.Count > 0)
                desired = desired.Where(pid => supported.Contains(pid)).ToList();
            else
                desired = desired.Where(pid => _adapter!.SupportsPid(pid)).ToList();

            if (desired.Count == 0)
            {
                var fb = new List<string>();
                if (_adapter!.SupportsPid("010C")) fb.Add("010C");
                if (_adapter!.SupportsPid("010D")) fb.Add("010D");
                if (fb.Count == 0) return;
                desired = fb;
            }

            var fast = desired.Intersect(new[] { "010C", "0111", "010D" }).ToList();
            var medium = desired.Intersect(new[] { "0105", "010F", "0104", "010B", "010E", "0110", "0133", "0122" }).ToList();
            var slow = desired.Intersect(new[] { "012F", "015C", "010A", "0123", "012C", "012D", "012E", "0130", "0131", "0132", "0121" }).ToList();

            var remaining = desired.Except(fast.Concat(medium).Concat(slow)).ToList();
            medium.AddRange(remaining);

            CurrentPollPlan = desired.ToList();
            Log($"📋 Poll plan → fast[{string.Join(",", fast)}], medium[{string.Join(",", medium)}], slow[{string.Join(",", slow)}]");

            TimeSpan fastInterval;
            TimeSpan medInterval;
            TimeSpan slowInterval;
            int gapFastMs;
            int gapMedMs;
            int gapSlowMs;

            if (isIso)
            {
                fastInterval = TimeSpan.FromMilliseconds(160);
                medInterval = TimeSpan.FromMilliseconds(400);
                slowInterval = TimeSpan.FromMilliseconds(1100);

                gapFastMs = 15;
                gapMedMs = 35;
                gapSlowMs = 60;
            }
            else
            {
                fastInterval = TimeSpan.FromMilliseconds(120);
                medInterval = TimeSpan.FromMilliseconds(300);
                slowInterval = TimeSpan.FromMilliseconds(900);

                gapFastMs = 10;
                gapMedMs = 20;
                gapSlowMs = 30;
            }

            int strikeLimit = isIso ? 10 : NoDataStrikeLimit;
            var corePids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "010C", "010D" };

            _noDataStrikes.Clear();

            CarData? _last = LastSnapshot;

            if (LastSnapshot == null)
            {
                LastSnapshot = new CarData { LastUpdated = DateTime.Now };
                onData?.Invoke(LastSnapshot);
                OnData?.Invoke(LastSnapshot);
            }

            async Task<ObdResponse> QueryOnceWithRetryAsync(string pid, int timeout)
            {
                var r = await _adapter!.SendCommandAsync(pid, timeoutMs: timeout, retryStoppedOnce: true);
                var rawU = (r.RawResponse ?? string.Empty).ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(rawU) || rawU.Contains("NO DATA") || rawU.Contains("?"))
                {
                    await Task.Delay(isIso ? 120 : 50, token);
                    r = await _adapter!.SendCommandAsync(pid, timeoutMs: timeout, retryStoppedOnce: true);
                }
                return r;
            }

            async Task PollGroupAsync(List<string> group, CarData snap, int gapMs)
            {
                foreach (var pid in group.ToList())
                {
                    if (!_registry.TryCreate(pid, out var datum)) continue;

                    var resp = await QueryOnceWithRetryAsync(pid, _pidTimeoutMs);
                    var rawU = (resp.RawResponse ?? string.Empty).ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(rawU) || rawU.Contains("NO DATA") || rawU.Contains("?"))
                    {
                        var strikes = _noDataStrikes.TryGetValue(pid, out var s) ? s + 1 : 1;
                        _noDataStrikes[pid] = strikes;

                        if (strikes >= strikeLimit && !corePids.Contains(pid))
                        {
                            if (fast.Remove(pid)) medium.Add(pid);
                            else if (medium.Remove(pid)) slow.Add(pid);
                            CurrentPollPlan = fast.Concat(medium).Concat(slow).ToList();
                            Log($"⏭️ Backing off {pid} (misses {strikes}x) — reducing cadence instead of disabling.");
                        }
                    }
                    else
                    {
                        _noDataStrikes[pid] = 0;
                        datum.Parse(resp.RawResponse!);
                        datum.ApplyTo(snap);
                    }

                    if (gapMs > 0)
                    {
                        try { await Task.Delay(gapMs, token); } catch { return; }
                    }
                }
            }

            _loopRunning = true;
            try
            {
                var lastFast = DateTime.MinValue;
                var lastMed = DateTime.MinValue;
                var lastSlow = DateTime.MinValue;

                while (!token.IsCancellationRequested)
                {
                    if (_adapter is null || !_adapter.IsConnected || SimulationMode) break;

                    var now = DateTime.UtcNow;

                    var snap = _last is null ? new CarData() : new CarData
                    {
                        RPM = _last.RPM,
                        Speed = _last.Speed,
                        ThrottlePercent = _last.ThrottlePercent,

                        FuelLevelPercent = _last.FuelLevelPercent,
                        CoolantTempF = _last.CoolantTempF,
                        OilTempF = _last.OilTempF,
                        IntakeTempF = _last.IntakeTempF,

                        EngineLoadPercent = _last.EngineLoadPercent,
                        FuelPressureKpa = _last.FuelPressureKpa,
                        MapKpa = _last.MapKpa,
                        TimingAdvanceDeg = _last.TimingAdvanceDeg,
                        MafGramsPerSec = _last.MafGramsPerSec,
                        BaroKPa = _last.BaroKPa,
                        FuelRailPressureRelKPa = _last.FuelRailPressureRelKPa,
                        FuelRailGaugePressureKPa = _last.FuelRailGaugePressureKPa,
                        CommandedEgrPercent = _last.CommandedEgrPercent,
                        EgrErrorPercent = _last.EgrErrorPercent,
                        CommandedEvapPurgePercent = _last.CommandedEvapPurgePercent,
                        EvapVaporPressurePa = _last.EvapVaporPressurePa,
                        WarmUpsSinceClear = _last.WarmUpsSinceClear,
                        DistanceWithMilKm = _last.DistanceWithMilKm,
                        DistanceSinceClearKm = _last.DistanceSinceClearKm,
                        LastUpdated = DateTime.Now
                    };

                    bool didFast = false;

                    if (fast.Count > 0 && (now - lastFast) >= fastInterval)
                    {
                        await PollGroupAsync(fast, snap, gapFastMs);
                        lastFast = now;
                        didFast = true;
                    }

                    if (medium.Count > 0 && (now - lastMed) >= medInterval)
                    {
                        await PollGroupAsync(medium, snap, gapMedMs);
                        lastMed = DateTime.UtcNow;
                    }

                    if (slow.Count > 0 && (now - lastSlow) >= slowInterval)
                    {
                        await PollGroupAsync(slow, snap, gapSlowMs);
                        lastSlow = DateTime.UtcNow;
                    }

                    if (didFast)
                    {
                        LastSnapshot = snap;
                        _last = snap;
                        onData?.Invoke(snap);
                        OnData?.Invoke(snap);

                        if (_rec.IsRecording) await _rec.AppendAsync(snap);

                        foreach (var k in _noDataStrikes.Keys.ToList())
                            if (_noDataStrikes[k] > 0) _noDataStrikes[k]--;
                    }

                    try { await Task.Delay(15, token); } catch { break; }
                }
            }
            finally
            {
                _loopRunning = false;
                Log("🛑 Live data loop ended.");
            }
        }

        public async Task EnsureLiveLoopRunningAsync(Action<CarData>? onData = null)
        {
            if (_loopRunning)
            {
                if (LastSnapshot != null) onData?.Invoke(LastSnapshot);
                return;
            }
            await StartLiveDataLoopAsync(onData);
        }

        public async Task<DtcSnapshot> ReadTroubleCodesAsync()
        {
            if (_adapter == null || !_adapter.IsConnected)
                return new DtcSnapshot();
            return await _adapter.ReadDtcAsync();
        }

        public async Task<bool> ClearTroubleCodesAsync()
        {
            if (_adapter == null || !_adapter.IsConnected)
                return false;

            var ok = await _adapter.ClearDtcAsync();
            if (ok)
                Log("🧹 DTCs cleared (Mode 04). Monitors may reset until drive cycle completes.");
            else
                Log("⚠️ Failed to clear DTCs.");
            return ok;
        }

        // ---------- Adapter discovery ----------

        private async Task<bool> EnsureAdapterAsync()
        {
            if (_adapter != null) return true;

            await _profiles.LoadAsync();
            var profile = _profiles.Current;

            return await TryEnsureBleAdapterAsync(profile);
        }

        private async Task<bool> TryEnsureBleAdapterAsync(VehicleProfile? profile)
        {
            Log("🔍 Scanning for ELM327 BLE devices...");

            Plugin.BLE.Abstractions.Contracts.IDevice? device = null;

            var preferred = profile?.PreferredAdapterName?.Trim();
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                device = await _bluetooth.FindDeviceAsync(preferred, preferred);
                if (device != null)
                    Log($"✅ Found preferred adapter: {device.Name}");
                else
                    Log($"⚠️ Preferred adapter '{preferred}' not found — trying common names...");
            }

            if (device == null)
            {
                var patterns = new (string a, string b)[] { ("VEEPEAK", "OBD"), ("OBDII", "ELM"), ("V-LINK", "OBD"), ("FIXD", "OBD") };
                foreach (var (a, b) in patterns)
                {
                    device = await _bluetooth.FindDeviceAsync(a, b);
                    if (device != null)
                    {
                        Log($"✅ Found device by pattern: {device.Name}");
                        break;
                    }
                }
            }

            if (device == null)
            {
                Log("❌ No compatible BLE device found. Staying non-live (no ECU data).");
                SimulationMode = true;
                IsAdapterConnected = false;
                IsEcuConnected = false;
                _adapter = null;
                Notify();
                return false;
            }

            _adapter = new Elm327Adapter(device, _profiles);
            _adapter.OnLog += Log;

            IsAdapterConnected = false;
            IsEcuConnected = false;
            Notify();
            return true;
        }

        // ---------- Misc helpers ----------

        private static bool SameSet(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            if (a.Count != b.Count) return false;
            var sa = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            sa.SymmetricExceptWith(b);
            return sa.Count == 0;
        }

        private static string? NormalizeProtocol(string? proto)
        {
            if (string.IsNullOrWhiteSpace(proto)) return null;
            var p = proto.Trim();

            if (p.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                var parts = p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2) return parts[1];
                return null;
            }

            return p;
        }

        private static string? MapProtoToAtsp(string? proto)
        {
            if (string.IsNullOrWhiteSpace(proto)) return null;
            var p = proto.ToUpperInvariant();

            if (p.Contains("9141")) return "ATSP3";
            if (p.Contains("14230") || p.Contains("KWP")) return "ATSP4";
            if (p.Contains("15765") || p.Contains("CAN")) return "ATSP6";

            return null;
        }

        private async Task ApplyProfileProtocolHintAsync()
        {
            try
            {
                await _profiles.LoadAsync();
                var protoRaw = _profiles.Current?.ProtocolDetected;
                var proto = NormalizeProtocol(protoRaw);
                if (string.IsNullOrWhiteSpace(proto))
                {
                    Log("🧭 No saved protocol hint; staying AUTO.");
                    return;
                }

                var atsp = MapProtoToAtsp(proto);
                if (string.IsNullOrWhiteSpace(atsp))
                {
                    Log($"🧭 Protocol hint not mappable: '{proto}'. Staying AUTO.");
                    return;
                }

                await _adapter!.SendCommandAsync(atsp);
                Log($"🧭 Profile protocol hint → {atsp} ({proto})");

                try
                {
                    var dp = await _adapter.SendCommandAsync("ATDP");
                    var active = dp.RawResponse?.Trim();
                    if (!string.IsNullOrWhiteSpace(active))
                        Log($"📡 ELM describe protocol: {active}");
                }
                catch { /* ignore */ }

                var up = proto.ToUpperInvariant();
                bool likelyIso = (up.Contains("9141") || up.Contains("14230") || up.Contains("KWP")) && !up.Contains("CAN");
                int probeTimeout = likelyIso ? 1800 : 600;

                var res = await _adapter.SendCommandAsync("0100", timeoutMs: probeTimeout, retryStoppedOnce: true);
                var raw = (res.RawResponse ?? string.Empty).ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(raw) || raw.Contains("NO DATA") || raw.Contains("?"))
                {
                    await _adapter.SendCommandAsync("ATSP0");
                    Log("↩️ Hint didn’t take; fell back to AUTO.");
                }
                else
                {
                    _isIso = likelyIso;
                    _pidTimeoutMs = _isIso ? 1500 : 500;
                }
            }
            catch (Exception ex)
            {
                Log($"(protocol hint skipped: {ex.Message})");
            }
        }

        private void CancelLiveLoop()
        {
            try { _liveCts?.Cancel(); } catch { /* ignore */ }
            _liveCts = null;
        }

        private static string VehicleKey(VehicleProfile? vp) =>
            vp is null ? "unknown" : $"{vp.Year}|{vp.Make}|{vp.Model}|{vp.Engine}".ToUpperInvariant();

        private void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _log.Info(line);
            lock (_logLock)
            {
                _logQ.Enqueue(line);
                while (_logQ.Count > MaxLogLines) _logQ.Dequeue();
            }
            OnLog?.Invoke(line);
        }
    }
}