using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;
using CarSpec.Utils.OBDData;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// High-level orchestrator that discovers an ELM327 adapter, connects,
    /// and (optionally) polls live OBD-II data using the OBDData registry.
    /// </summary>
    public class ObdConnectionService
    {
        private readonly BluetoothManager _bluetooth;
        private readonly IVehicleProfileService _profiles;
        private Elm327Adapter? _adapter;
        private ObdService? _obdService;
        private readonly Logger _log = new("OBD-CONNECT");
        private CancellationTokenSource? _liveCts;

        private readonly ObdDataRegistry _registry = ObdDataRegistry.Default;

        // Capability + resilience
        private HashSet<string> _supportedPids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _noDataStrikes = new(StringComparer.OrdinalIgnoreCase);
        private const int NoDataStrikeLimit = 3;

        public VehicleProfile? CurrentProfile => _profiles.Current;

        public bool SimulationMode { get; set; } = true;
        public bool IsConnected => _adapter?.IsConnected ?? false;
        public bool IsConnecting { get; private set; }
        public bool IsAdapterConnected { get; private set; }
        public bool IsEcuConnected { get; private set; }

        private int _pidTimeoutMs = 600;
        private CarData? _lastSnapshot;
        private EcuFingerprint? _lastFp;
        public EcuFingerprint? LastFingerprint => _adapter?.LastFingerprint;
        public event Action<EcuFingerprint?>? OnFingerprint;

        /// <summary>For telemetry/UX: last computed poll plan.</summary>
        public IReadOnlyList<string> CurrentPollPlan { get; private set; } = new List<string>();

        public event Action<string>? OnLog;

        public ObdConnectionService(IVehicleProfileService profiles)
        {
            _profiles = profiles;
            _bluetooth = new BluetoothManager();
            _bluetooth.OnLog += Log;
        }

        // ----- Public connect/disconnect -----

        public async Task<bool> AutoConnectAsync()
        {
            if (!await EnsureAdapterAsync())
                return false;

            return await ConnectAsync();
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                IsConnecting = true;
                Log("🔌 Starting ELM327 connection...");

                if (!await EnsureAdapterAsync())
                    return false;

                var ok = await _adapter!.ConnectAsync();
                IsAdapterConnected = _adapter.IsConnected;
                IsEcuConnected = _adapter.IsEcuAwake;

                if (!ok || !_adapter.IsConnected)
                {
                    Log("❌ Adapter connect failed.");
                    SimulationMode = true;
                    _obdService = null;
                    return false;
                }

                if (!_adapter.IsEcuAwake)
                {
                    Log("⚠️ ECU not responding — staying in Simulation Mode.");
                    SimulationMode = true;
                    _obdService = null;
                    return true; // adapter connected, ECU not awake (still useful)
                }

                // >>> ECU is awake here <<<

                // 1) Immediately notify UI with whatever the adapter already has
                //    (ConnectAsync inside Elm327Adapter sets LastFingerprint after 4100 succeeds)
                OnFingerprint?.Invoke(_adapter.LastFingerprint);

                // 2) Build high-level service and flip to live
                _obdService = new ObdService(_adapter);
                SimulationMode = false;
                Log("✅ Live OBD connected.");

                // 3) (Optional) If adapter did not populate a fingerprint yet, read it now,
                //    then notify UI again with the fresh data.
                try
                {
                    _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync();
                    OnFingerprint?.Invoke(_lastFp);

                    if (_lastFp?.Vin != null)
                        Log($"🪪 VIN: {_lastFp.Vin} (Year≈{_lastFp.Year?.ToString() ?? "?"}, Protocol={_lastFp.Protocol})");

                    await _profiles.LoadAsync();
                    var catalog = await _profiles.GetAllAsync();
                    if (_lastFp is not null && catalog is { Count: > 0 })
                    {
                        var bestMatch = ChooseBestProfile(_lastFp, catalog);
                        var current = _profiles.Current;

                        if (current != null && bestMatch != null && !ReferenceEquals(bestMatch, current))
                        {
                            var curScore = SimilarityScore(current, _lastFp);
                            var bestScore = SimilarityScore(bestMatch, _lastFp);
                            Log($"🧪 Profile match scores → Current='{current.Year} {current.Make} {current.Model}'={curScore}, Suggested='{bestMatch.Year} {bestMatch.Make} {bestMatch.Model}'={bestScore}");

                            if (bestScore >= curScore + 20 && bestScore >= 50)
                            {
                                Log("🤖 Switching profile based on ECU fingerprint.");
                                await _profiles.SetCurrentAsync(bestMatch);
                            }
                            else if (bestScore > curScore)
                            {
                                Log("💡 Suggestion: selected profile may be wrong; consider switching to suggested.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"ℹ️ Fingerprint step skipped ({ex.Message}).");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ Connect exception: {ex.Message}");
                _obdService = null;
                return false;
            }
            finally
            {
                IsConnecting = false;
            }
        }

        public Task Disconnect()
        {
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

            Log("🧹 Disconnection complete, back to Simulation Mode.");
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

                if (_obdService == null)
                    _obdService = new ObdService(_adapter);

                if (_obdService == null) _obdService = new ObdService(_adapter);
                _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync();

                if (_lastFp?.Vin != null)
                    Log($"🪪 VIN: {_lastFp.Vin} (Year≈{_lastFp.Year?.ToString() ?? "?"}, Protocol={_lastFp.Protocol})");

                await _profiles.LoadAsync();
                var all = await _profiles.GetAllAsync();
                var best = ChooseBestProfile(_lastFp!, all);
                var cur = _profiles.Current;

                return true;
            }

            Log("⚠️ ECU still not responding — car may be off.");
            return false;
        }

        public async Task<CarData> GetLatestDataAsync()
        {
            if (SimulationMode || _obdService == null)
                return CarData.Simulated();

            return await _obdService.GetLatestDataAsync();
        }

        // ----- Capability discovery -----

        private async Task EnsureCapabilitiesAsync()
        {
            // We always prefer live capability from adapter. (No cache/persistence here.)
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

            // If we got here, we couldn't read capability. Keep whatever we had (maybe empty).
            if (_supportedPids.Count == 0)
                Log("ℹ️ No supported PID map available yet; will probe optimistically.");
        }

        public IReadOnlySet<string> GetSupportedPids() => _supportedPids;

        // ----- Live polling using registry -----

        public async Task StartLiveDataLoopAsync(Action<CarData>? onData = null)
        {
            CancelLiveLoop();
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;

            if (_adapter == null || !_adapter.IsConnected || SimulationMode)
            {
                Log("⚠️ Cannot start live data loop — adapter not connected or in Simulation Mode.");
                return;
            }

            await _profiles.LoadAsync();
            await EnsureCapabilitiesAsync();
            var profile = _profiles.Current;

            // Build desired list (same as you already do)
            var desired = (profile?.DesiredMode01Pids ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (desired.Count == 0)
                desired = new() { "010C", "0111", "010D", "0105", "010F", "012F", "015C" };

            desired = desired.Where(pid => _registry.TryCreate(pid, out _)).ToList();
            desired = (_supportedPids.Count > 0)
                ? desired.Where(pid => _supportedPids.Contains(pid)).ToList()
                : desired.Where(pid => _adapter!.SupportsPid(pid)).ToList();

            if (desired.Count == 0)
            {
                var fb = new List<string>();
                if (_adapter!.SupportsPid("010C")) fb.Add("010C");
                if (_adapter!.SupportsPid("010D")) fb.Add("010D");
                if (fb.Count == 0) return;
                desired = fb;
            }

            // ----- GROUPS -----
            // Fast group: keep these aligned (RPM + TPS + Speed).
            var fast = desired.Intersect(new[] { "010C", "0111", "010D" }).ToList();
            // Medium cadence: temps (coolant, IAT).
            var medium = desired.Intersect(new[] { "0105", "010F" }).ToList();
            // Slow cadence: fuel level, oil temp (often slow/unsupported).
            var slow = desired.Intersect(new[] { "012F", "015C" }).ToList();

            // Anything left (if user added custom) goes to medium by default.
            var remaining = desired.Except(fast.Concat(medium).Concat(slow)).ToList();
            medium.AddRange(remaining);

            // Publish plan so the UI knows which tiles to render
            CurrentPollPlan = desired.ToList();
            Log($"📋 Poll plan → fast[{string.Join(",", fast)}], medium[{string.Join(",", medium)}], slow[{string.Join(",", slow)}]");

            // Cadences (tune to taste)
            var fastInterval = TimeSpan.FromMilliseconds(120);  // target “frame rate” for RPM/TPS/Speed
            var medInterval = TimeSpan.FromMilliseconds(300);
            var slowInterval = TimeSpan.FromMilliseconds(900);

            // Tiny spacing to avoid frame glomming; keep intra-group gap small
            const int gapFastMs = 12;
            const int gapMedMs = 18;
            const int gapSlowMs = 22;

            var lastFast = DateTime.MinValue;
            var lastMed = DateTime.MinValue;
            var lastSlow = DateTime.MinValue;

            _noDataStrikes.Clear();

            CarData? _last = _lastSnapshot; // use your cache to prevent flicker

            async Task PollGroupAsync(List<string> group, CarData snap, int gapMs)
            {
                foreach (var pid in group.ToList())
                {
                    if (!_registry.TryCreate(pid, out var datum)) continue;
                    var resp = await _adapter!.SendCommandAsync(pid, timeoutMs: 1200, retryStoppedOnce: true);
                    var raw = (resp.RawResponse ?? string.Empty).ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(raw) || raw.Contains("NO DATA") || raw.Contains("?"))
                    {
                        var strikes = _noDataStrikes.TryGetValue(pid, out var s) ? s + 1 : 1;
                        _noDataStrikes[pid] = strikes;
                        if (strikes >= NoDataStrikeLimit)
                        {
                            // Disable for this session; also remove from groups
                            fast.Remove(pid); medium.Remove(pid); slow.Remove(pid);
                            var newPlan = fast.Concat(medium).Concat(slow).ToList();
                            CurrentPollPlan = newPlan;
                            Log($"⏭️ Disabling {pid} (no data {strikes}x).");
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

            try
            {
                var sw = new System.Diagnostics.Stopwatch();
                sw.Start();

                while (!token.IsCancellationRequested)
                {
                    if (_adapter is null || !_adapter.IsConnected || SimulationMode) break;

                    var now = DateTime.UtcNow;

                    // Start by carrying forward last snapshot (prevents 0 flicker)
                    var snap = _last is null ? new CarData() : new CarData
                    {
                        RPM = _last.RPM,
                        Speed = _last.Speed,
                        ThrottlePercent = _last.ThrottlePercent,
                        FuelLevelPercent = _last.FuelLevelPercent,
                        CoolantTempF = _last.CoolantTempF,
                        OilTempF = _last.OilTempF,
                        IntakeTempF = _last.IntakeTempF
                    };
                    snap.LastUpdated = DateTime.Now;

                    bool didFast = false;

                    // FAST group – always if interval elapsed
                    if (fast.Count > 0 && (now - lastFast) >= fastInterval)
                    {
                        await PollGroupAsync(fast, snap, gapFastMs);
                        lastFast = now;
                        didFast = true;
                    }

                    // MEDIUM group – only when its own interval elapses
                    if (medium.Count > 0 && (now - lastMed) >= medInterval)
                    {
                        await PollGroupAsync(medium, snap, gapMedMs);
                        lastMed = DateTime.UtcNow;
                    }

                    // SLOW group – less frequent
                    if (slow.Count > 0 && (now - lastSlow) >= slowInterval)
                    {
                        await PollGroupAsync(slow, snap, gapSlowMs);
                        lastSlow = DateTime.UtcNow;
                    }

                    // Only publish after FAST group to keep RPM/TPS aligned in the same tick
                    if (didFast)
                    {
                        _lastSnapshot = snap;
                        _last = snap;
                        onData?.Invoke(snap);
                    }

                    // Small idle to avoid spinning if fast interval is very low
                    try { await Task.Delay(15, token); } catch { break; }
                }
            }
            finally
            {
                Log("🛑 Live data loop ended.");
            }
        }

        // ----- Adapter discovery -----

        private async Task<bool> EnsureAdapterAsync()
        {
            if (_adapter != null) return true;

            await _profiles.LoadAsync();
            var profile = _profiles.Current;

            var transport = (profile?.PreferredTransport ?? "BLE").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(transport)) transport = "BLE";

            switch (transport)
            {
                case "BLE":
                    return await TryEnsureBleAdapterAsync(profile);

                case "WIFI":
                case "WI-FI":
                case "WI_FI":
                case "USB":
                case "SERIAL":
                    Log($"ℹ️ Requested transport '{transport}' not implemented yet — falling back to BLE.");
                    return await TryEnsureBleAdapterAsync(profile);

                default:
                    Log($"ℹ️ Unknown transport '{transport}' — falling back to BLE.");
                    return await TryEnsureBleAdapterAsync(profile);
            }
        }

        private async Task<bool> TryEnsureBleAdapterAsync(VehicleProfile? profile)
        {
            Log("🔍 Scanning for ELM327 BLE devices...");

            Plugin.BLE.Abstractions.Contracts.IDevice? device = null;

            // 1) Preferred adapter name (exact match)
            var preferred = profile?.PreferredAdapterName?.Trim();
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                device = await _bluetooth.FindDeviceAsync(preferred, preferred);
                if (device != null)
                    Log($"✅ Found preferred adapter: {device.Name}");
                else
                    Log($"⚠️ Preferred adapter '{preferred}' not found — trying common names...");
            }

            // 2) Common patterns
            if (device == null)
            {
                var pairs = new (string a, string b)[] { ("VEEPEAK", "OBD"), ("OBDII", "ELM"), ("V-LINK", "OBD") };
                foreach (var (a, b) in pairs)
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
                Log("❌ No compatible BLE device found. Staying in Simulation Mode.");
                SimulationMode = true;
                IsAdapterConnected = false;
                IsEcuConnected = false;
                _adapter = null;
                return false;
            }

            _adapter = new Elm327Adapter(device, _profiles);
            _adapter.OnLog += Log;

            IsAdapterConnected = false;
            IsEcuConnected = false;
            return true;
        }

        private static int SimilarityScore(VehicleProfile vp, EcuFingerprint fp)
        {
            int score = 0;

            // WMI → Make (very rough; extend as you add more)
            if (!string.IsNullOrWhiteSpace(fp.Wmi) && !string.IsNullOrWhiteSpace(vp.Make))
            {
                if (fp.Wmi.StartsWith("JF", StringComparison.OrdinalIgnoreCase) &&
                    vp.Make.Equals("Subaru", StringComparison.OrdinalIgnoreCase)) score += 30;

                if (fp.Wmi.StartsWith("J", StringComparison.OrdinalIgnoreCase) &&
                    vp.Make.Equals("Toyota", StringComparison.OrdinalIgnoreCase)) score += 20;
            }

            // Year closeness
            if (fp.Year is int y && int.TryParse(vp.Year, out var vy))
                score += (Math.Abs(y - vy) <= 1) ? 25 : (Math.Abs(y - vy) <= 3 ? 10 : 0);

            // Protocol CAN vs not-CAN (coarse but useful)
            if (!string.IsNullOrWhiteSpace(fp.Protocol) && !string.IsNullOrWhiteSpace(vp.ProtocolHint))
            {
                bool ecuCan = fp.Protocol.Contains("CAN", StringComparison.OrdinalIgnoreCase);
                bool vpCan = vp.ProtocolHint.Contains("CAN", StringComparison.OrdinalIgnoreCase);
                if (ecuCan == vpCan) score += 15;
            }

            // Desired PID overlap
            var desired = (vp.DesiredMode01Pids ?? new()).Select(p => p.Trim().ToUpperInvariant());
            int overlap = desired.Count(pid => fp.SupportedPids.Contains(pid));
            score += Math.Min(30, overlap * 5);

            return score; // ~0..100
        }

        private VehicleProfile ChooseBestProfile(EcuFingerprint fp, IEnumerable<VehicleProfile> all)
        {
            VehicleProfile? best = null; int bestScore = int.MinValue;
            foreach (var vp in all)
            {
                var s = SimilarityScore(vp, fp);
                if (s > bestScore) { best = vp; bestScore = s; }
            }
            return best ?? _profiles.Current!;
        }

        // ----- Misc helpers -----

        private void CancelLiveLoop()
        {
            try { _liveCts?.Cancel(); } catch { /* ignore */ }
            _liveCts = null;
        }

        private static string VehicleKey(VehicleProfile? vp) =>
            vp is null ? "unknown" : $"{vp.Year}|{vp.Make}|{vp.Model}|{vp.Engine}".ToUpperInvariant();

        private void Log(string msg)
        {
            _log.Info(msg);
            OnLog?.Invoke(msg);
        }
    }
}