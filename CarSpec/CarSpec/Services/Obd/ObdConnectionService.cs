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
                OnFingerprint?.Invoke(_adapter.LastFingerprint);

                // 2) Build high-level service and flip to live
                _obdService = new ObdService(_adapter);
                SimulationMode = false;
                Log("✅ Live OBD connected.");

                // 3) Ensure we have a fingerprint, notify UI, match profile… and LEARN
                try
                {
                    _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync();
                    OnFingerprint?.Invoke(_lastFp);

                    if (_lastFp is not null)
                        await _profiles.LearnFromFingerprintAsync(_lastFp, transport: "BLE");

                    if (_lastFp?.Vin != null)
                        Log($"🪪 VIN: {_lastFp.Vin} (Year≈{_lastFp.Year?.ToString() ?? "?"}, Protocol={_lastFp.Protocol})");

                    // --------- CHANGES START: learn & stamp transport ----------
                    await _profiles.LoadAsync();
                    if (_profiles.Current is VehicleProfile cur && _lastFp is not null)
                    {
                        // If user didn’t set a transport in setup, stamp what we actually used
                        if (string.IsNullOrWhiteSpace(cur.PreferredTransport))
                        {
                            cur.PreferredTransport = "BLE"; // we connected via BLE path
                            await _profiles.SetCurrentAsync(cur); // persist that field
                        }

                        // Cache protocol/VIN/supported PIDs/etc for faster next time
                        await _profiles.LearnFromFingerprintAsync(_lastFp);
                    }
                    // --------- CHANGES END ----------

                    // Optional: suggest switching to a better-matching profile
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

                try
                {
                    _lastFp = _adapter.LastFingerprint ?? await _adapter.ReadFingerprintAsync();
                    OnFingerprint?.Invoke(_lastFp);

                    if (_lastFp?.Vin != null)
                        Log($"🪪 VIN: {_lastFp.Vin} (Year≈{_lastFp.Year?.ToString() ?? "?"}, Protocol={_lastFp.Protocol})");

                    // Persist the learned data (transport, protocol, VIN, PID map, etc.)
                    await _profiles.LearnFromFingerprintAsync(_lastFp!, transport: "BLE");
                }
                catch (Exception ex)
                {
                    Log($"ℹ️ Fingerprint caching skipped: {ex.Message}");
                }

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

            // Build a single "supported" set: live map if present, else cached from profile
            var supported = (_supportedPids.Count > 0)
                ? _supportedPids
                : (profile?.SupportedPidsCache?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            // Build desired list
            var desired = (profile?.DesiredMode01Pids ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (desired.Count == 0)
                desired = new() { "010C", "0111", "010D", "0105", "010F", "012F", "015C" };

            // Keep only PIDs we can parse
            desired = desired.Where(pid => _registry.TryCreate(pid, out _)).ToList();

            // Filter using the unified "supported" set. If we don't have one, probe via adapter.
            // ★ CHANGED: removed the duplicate filter that re-did this step.
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

            // ----- GROUPS -----
            var fast = desired.Intersect(new[] { "010C", "0111", "010D" }).ToList(); // RPM/TPS/Speed
            var medium = desired.Intersect(new[] { "0105", "010F" }).ToList();         // Coolant/IAT
            var slow = desired.Intersect(new[] { "012F", "015C" }).ToList();         // Fuel/Oil

            var remaining = desired.Except(fast.Concat(medium).Concat(slow)).ToList();
            medium.AddRange(remaining);

            CurrentPollPlan = desired.ToList();
            Log($"📋 Poll plan → fast[{string.Join(",", fast)}], medium[{string.Join(",", medium)}], slow[{string.Join(",", slow)}]");

            // Cadences
            var fastInterval = TimeSpan.FromMilliseconds(120);
            var medInterval = TimeSpan.FromMilliseconds(300);
            var slowInterval = TimeSpan.FromMilliseconds(900);

            const int gapFastMs = 12;
            const int gapMedMs = 18;
            const int gapSlowMs = 22;

            var lastFast = DateTime.MinValue;
            var lastMed = DateTime.MinValue;
            var lastSlow = DateTime.MinValue;

            _noDataStrikes.Clear();

            CarData? _last = _lastSnapshot;

            async Task PollGroupAsync(List<string> group, CarData snap, int gapMs)
            {
                foreach (var pid in group.ToList())
                {
                    if (!_registry.TryCreate(pid, out var datum)) continue;

                    // ★ CHANGED: honor adapter/profile-tuned timeout
                    var resp = await _adapter!.SendCommandAsync(pid, retryStoppedOnce: true);
                    var raw = (resp.RawResponse ?? string.Empty).ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(raw) || raw.Contains("NO DATA") || raw.Contains("?"))
                    {
                        var strikes = _noDataStrikes.TryGetValue(pid, out var s) ? s + 1 : 1;
                        _noDataStrikes[pid] = strikes;

                        if (strikes >= NoDataStrikeLimit)
                        {
                            fast.Remove(pid); medium.Remove(pid); slow.Remove(pid);
                            // ★ OPTIONAL: refresh published plan so UI can hide tiles
                            CurrentPollPlan = fast.Concat(medium).Concat(slow).ToList();
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

                    // Carry forward last snapshot (prevents 0 flicker)
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

                    // Publish after FAST to keep RPM/TPS aligned
                    if (didFast)
                    {
                        _lastSnapshot = snap;
                        _last = snap;
                        onData?.Invoke(snap);
                    }

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

            // We currently support BLE only (Wi-Fi/USB not wired up yet)
            return await TryEnsureBleAdapterAsync(profile);
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
                var patterns = new (string a, string b)[] { ("VEEPEAK", "OBD"), ("OBDII", "ELM"), ("V-LINK", "OBD") };
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