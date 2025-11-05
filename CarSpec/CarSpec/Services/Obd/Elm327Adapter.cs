using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CarSpec.Interfaces;
using CarSpec.Models;
using CarSpec.Services.Bluetooth;
using CarSpec.Utils;

namespace CarSpec.Services.Obd
{
    /// <summary>
    /// Handles low-level communication with the ELM327 adapter (sending AT/PID commands).
    /// </summary>
    public class Elm327Adapter : IDisposable
    {
        private readonly IObdTransport _transport;
        private readonly IVehicleProfileService _profiles;
        private readonly Logger _log = new("ELM327");

        // Serialize all adapter I/O to avoid interleaved reads
        private readonly SemaphoreSlim _ioLock = new(1, 1);

        public event Action<string>? OnLog;
        public bool IsConnected { get; private set; }
        public bool IsEcuAwake { get; private set; }
        public EcuFingerprint? LastFingerprint { get; private set; }

        private bool _isIso9141;
        private int _pidTimeoutMs = 600;

        // PID support bitmaps: "0100","0120","0140","0160","0180" -> 8 hex chars
        private readonly Dictionary<string, string> _pidSupport = new();

        public IReadOnlyDictionary<string, string> PidSupportSnapshot =>
            new Dictionary<string, string>(_pidSupport);

        public Elm327Adapter(object device, IVehicleProfileService profiles)
        {
            _profiles = profiles;
            _transport = new BleObdTransport(device);
        }

        public async Task<bool> ConnectAsync()
        {
            await _profiles.LoadAsync();
            var profile = _profiles.Current;

            // If the profile has learned hints, set them early
            if (!string.IsNullOrWhiteSpace(profile?.ProtocolDetected) && string.IsNullOrWhiteSpace(profile.ProtocolHint))
            {
                // Let ApplyProfileHintsAsync pick it up via ProtocolHint
                profile.ProtocolHint = profile.ProtocolDetected;
            }

            Log("🔌 Starting ELM327 connection...");
            _pidSupport.Clear();
            IsEcuAwake = false;

            try
            {
                Log("🔍 Initializing BLE transport connection...");
                Log($"🔍 Transport type: {_transport.GetType().Name}");

                IsConnected = await _transport.ConnectAsync();
                if (!IsConnected)
                {
                    Log("❌ Transport connection failed — BLE handshake unsuccessful.");
                    return false;
                }

                Log("✅ Connection established. Sending ELM327 base init...");

                // --- Base Initialization (safe + common) ---
                string[] baseCmds = { "ATZ", "ATE0", "ATL0", "ATS0" }; // Echo off, LF off, spaces off
                foreach (var cmd in baseCmds)
                {
                    var resp = await SendCommandAsync(cmd);
                    await Task.Delay(200);
                    var one = TrimOneLine(resp.RawResponse);
                    Log(!string.IsNullOrWhiteSpace(one)
                        ? $"✅ {cmd} → {one}"
                        : $"⚠️ {cmd} returned no data");
                }

                // Apply profile hints (protocol / init script / headers)
                await ApplyProfileHintsAsync(profile);
                await Task.Delay(300);

                // --- Protocol select / verify ---
                bool protocolOK = false;
                string[] protocolSequence = { "ATSP0", "ATSP3" /* ISO9141 slow cars */ };
                foreach (var proto in protocolSequence)
                {
                    Log($"🔧 Setting protocol: {proto}");
                    await SendCommandAsync(proto);
                    await Task.Delay(proto == "ATSP3" ? 800 : 400);

                    if (proto == "ATSP3") // ISO9141 slow init only makes sense here
                    {
                        Log("🕐 Attempting slow init for ISO9141-2...");
                        _ = await SendCommandAsync("ATSI"); // some firmwares may not support
                        await Task.Delay(3000);
                        await SendCommandAsync("ATST FF"); // long timeout
                        await Task.Delay(250);
                        await SendCommandAsync("ATH1");    // headers ON temporarily for visibility
                        await Task.Delay(150);
                    }

                    var protoResp = await SendCommandAsync("ATDP");
                    var line = TrimOneLine(protoResp.RawResponse);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Log($"📡 Active protocol: {line}");
                        protocolOK = true;
                        break;
                    }
                }

                if (!protocolOK)
                {
                    Log("⚠️ Protocol auto-detect inconclusive — forcing ISO9141-2 (ATSP3).");
                    await SendCommandAsync("ATSP3");
                    await Task.Delay(600);
                }

                // --- Wake ECU & read PID support (4100) with headers ON for clarity ---
                await SendCommandAsync("ATH1"); // ensure headers ON
                await Task.Delay(200);

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    var ecuResp = await SendCommandAsync("0100", timeoutMs: 12000);
                    var cleaned = CleanHex(ecuResp.RawResponse).ToUpperInvariant();

                    // Find '4100' + exactly 8 hex digits — ignore any noise
                    var m = Regex.Match(cleaned, @"4100([0-9A-F]{8})", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        IsEcuAwake = true;
                        var word = m.Groups[1].Value; // 8 hex chars
                        _pidSupport["0100"] = word;
                        LogPidSupportFrom4100("4100" + word);

                        // Read fingerprint now that ECU is awake
                        LastFingerprint = await ReadFingerprintAsync();
                        if (LastFingerprint?.Vin != null)
                            Log($"🪪 VIN={LastFingerprint.Vin}, Year≈{LastFingerprint.Year?.ToString() ?? "?"}, Protocol={LastFingerprint.Protocol}, PIDs={LastFingerprint.SupportedPids.Count}");

                        Log($"✅ ECU communication established on attempt {attempt}.");
                        break;
                    }

                    if (cleaned.Contains("BUSINIT", StringComparison.OrdinalIgnoreCase) ||
                        cleaned.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"⚠️ Attempt {attempt}: ECU still initializing → {cleaned}");
                        await Task.Delay(1000);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(cleaned))
                        Log($"⚠️ Attempt {attempt}: No response.");
                    else
                        Log($"⚠️ Attempt {attempt}: Unexpected reply → {cleaned}");
                }

                // Headers OFF for normal polling
                await SendCommandAsync("ATH0");
                await Task.Delay(150);

                if (!IsEcuAwake)
                {
                    Log("❌ ECU did not respond — staying connected to adapter, but ECU is asleep.");
                    // Return true because adapter is connected; caller can decide to simulate.
                    return true;
                }

                // Optionally fetch additional support blocks robustly
                foreach (var block in new[] { "0120", "0140", "0160", "0180" })
                {
                    var resp = await SendCommandAsync(block);
                    var txt = CleanHex(resp.RawResponse).ToUpperInvariant();
                    var sig = "41" + block[2..]; // e.g., 4120
                    var m2 = Regex.Match(txt, sig + @"([0-9A-F]{8})");
                    if (m2.Success)
                    {
                        _pidSupport[block] = m2.Groups[1].Value;
                        Log($"🧭 Support {block}: {m2.Groups[1].Value}");
                    }
                    await Task.Delay(150);
                }

                Log($"🧭 PID map keys: {string.Join(", ", _pidSupport.Keys)}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ ELM327 connection exception: {ex.Message}");
                return false;
            }
        }

        public IReadOnlySet<string> GetSupportedPids()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int[] starts = { 0x00, 0x20, 0x40, 0x60, 0x80 };

            foreach (var start in starts)
            {
                var key = "01" + start.ToString("X2"); // "0100","0120",...
                if (!_pidSupport.TryGetValue(key, out var hex) || string.IsNullOrWhiteSpace(hex))
                    continue;

                var cleaned = CleanHex(hex).ToUpperInvariant();
                if (cleaned.Length < 8) continue;

                var word = cleaned[..8];
                if (!uint.TryParse(word, NumberStyles.HexNumber, null, out var mask))
                    continue;

                for (int i = 0; i < 32; i++)
                {
                    bool bitOn = (mask & (1u << (31 - i))) != 0;
                    if (!bitOn) continue;

                    int pidVal = start + (i + 1); // 1..32 in this block
                    if (pidVal >= 0x01 && pidVal <= 0xA0)
                        result.Add("01" + pidVal.ToString("X2"));
                }
            }

            return result;
        }

        /// <summary>
        /// Send a raw AT/PID command and read until prompt '>' or timeout.
        /// Serialized by a lock to prevent interleaved replies.
        /// </summary>
        public async Task<ObdResponse> SendCommandAsync(string command, int timeoutMs = -1, bool retryStoppedOnce = true)
        {
            if (timeoutMs < 0) timeoutMs = _pidTimeoutMs; // use adapter-tuned default
            await _ioLock.WaitAsync();
            try
            {
                await _transport.WriteAsync(command + "\r");

                var sb = new StringBuilder();
                var limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                while (DateTime.UtcNow < limit)
                {
                    var chunk = await _transport.ReadAsync();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        sb.Append(chunk);
                        if (chunk.IndexOf('>') >= 0) break; // prompt received
                    }
                    await Task.Delay(8); // was 40
                }

                var response = sb.ToString().Trim();

                if (retryStoppedOnce && response.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log($"⚠️ ECU returned STOPPED for {command} → retrying once...");
                    await Task.Delay(200);
                    return await SendCommandAsync(command, timeoutMs, retryStoppedOnce: false);
                }

                return new ObdResponse { Command = command, RawResponse = response };
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// Map of Mode 01 PID support for blocks 0100,0120,0140,0160,0180.
        /// </summary>
        public bool SupportsPid(string pidHex)
        {
            if (string.IsNullOrWhiteSpace(pidHex) || pidHex.Length != 4) return false;
            var mode = pidHex[..2];  // "01"
            var pid = pidHex[2..];   // "0C"
            if (mode != "01") return false;

            if (!int.TryParse(pid, NumberStyles.HexNumber, null, out int pidVal))
                return false;

            int blockIndex = (pidVal - 1) / 0x20;           // 0..4
            int withinBlock = ((pidVal - 1) % 0x20) + 1;    // 1..32
            string key = "01" + (blockIndex * 0x20).ToString("X2"); // "0100","0120",...

            if (!_pidSupport.TryGetValue(key, out var hex) || string.IsNullOrWhiteSpace(hex)) return false;
            hex = CleanHex(hex);
            if (hex.Length < 8) return false;

            if (!uint.TryParse(hex[..8], NumberStyles.HexNumber, null, out var mask))
                return false;

            int bit = 32 - withinBlock; // MSB->PID01 ... LSB->PID20
            return (mask & (1u << bit)) != 0;
        }

        // -------- Fingerprint helpers --------

        public async Task<EcuFingerprint> ReadFingerprintAsync()
        {
            // Protocol (ATDP)
            var dp = await SendCommandAsync("ATDP");
            var protocol = TrimOneLine(dp.RawResponse);

            // Supported PIDs
            var supported = GetSupportedPids().ToHashSet(StringComparer.OrdinalIgnoreCase);

            // VIN (Mode 09 PID 02)
            var vin = await ReadVinAsync();
            string? wmi = null;
            int? year = null;
            if (!string.IsNullOrWhiteSpace(vin) && vin.Length >= 10)
            {
                wmi = vin.Substring(0, 3);
                year = DecodeModelYear(vin[9]);
            }

            // CAL IDs (Mode 09 PID 04) – optional
            var calIds = await ReadCalIdsAsync();

            return new EcuFingerprint
            {
                Protocol = protocol,
                SupportedPids = supported,
                Vin = vin,
                Wmi = wmi,
                Year = year,
                CalIds = calIds
            };
        }

        private static int? DecodeModelYear(char y)
        {
            const string seq = "ABCDEFGHJKLMNPRSTVWXY123456789";
            int idx = seq.IndexOf(char.ToUpperInvariant(y));
            if (idx < 0) return null;
            int year = 1980 + idx;
            while (year < 1990) year += 30;
            while (year > 2039) year -= 30;
            return year;
        }

        private async Task<string?> ReadVinAsync()
        {
            // Try with headers OFF first (normal polling)
            var vin = await ReadVinCoreAsync(timeoutMs: 2500);
            if (!string.IsNullOrEmpty(vin)) return vin;

            // Some ECUs behave better with headers ON and a longer timeout
            await SendCommandAsync("ATH1");
            await Task.Delay(120);
            vin = await ReadVinCoreAsync(timeoutMs: 4000);
            await SendCommandAsync("ATH0");
            return vin;
        }

        private async Task<string?> ReadVinCoreAsync(int timeoutMs)
        {
            var resp = await SendCommandAsync("0902", timeoutMs: timeoutMs);
            var raw = CleanHex(resp.RawResponse).ToUpperInvariant();

            // Keep only hex chars
            var hexOnly = new StringBuilder(raw.Length);
            foreach (char c in raw)
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')) hexOnly.Append(c);

            // Convert pairs to ASCII and keep only printable
            var bytes = new List<byte>();
            for (int i = 0; i + 1 < hexOnly.Length; i += 2)
            {
                string pair = hexOnly.ToString(i, 2);
                if (!byte.TryParse(pair, NumberStyles.HexNumber, null, out var b)) continue;
                if (b >= 0x20 && b <= 0x7E) bytes.Add(b);
            }
            var s = Encoding.ASCII.GetString(bytes.ToArray());
            s = new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

            // Look for a plausible 17-char VIN
            if (s.Length >= 17)
            {
                for (int i = 0; i + 16 < s.Length; i++)
                {
                    var cand = s.Substring(i, 17);
                    if (cand.All(ch => char.IsLetterOrDigit(ch))) return cand;
                }
            }
            return null; // gracefully fallback → “—” in UI
        }

        private async Task<List<string>> ReadCalIdsAsync()
        {
            // Mode 09 PID 04 (CAL IDs)
            var resp = await SendCommandAsync("0904", timeoutMs: 2500);
            var raw = CleanHex(resp.RawResponse).ToUpperInvariant();

            var hexOnly = new StringBuilder(raw.Length);
            foreach (char c in raw)
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')) hexOnly.Append(c);

            var bytes = new List<byte>();
            for (int i = 0; i + 1 < hexOnly.Length; i += 2)
            {
                string pair = hexOnly.ToString(i, 2);
                if (byte.TryParse(pair, NumberStyles.HexNumber, null, out var b) &&
                    b >= 0x20 && b <= 0x7E)
                {
                    bytes.Add(b);
                }
            }

            var ascii = Encoding.ASCII.GetString(bytes.ToArray());
            return ascii
                .Split(new[] { '\r', '\n', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Length >= 4)
                .ToList();
        }

        // -------- Helpers --------

        private static string CleanHex(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? string.Empty
                : s.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace(">", "");

        private static string TrimOneLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var line = s.Replace("\r", "\n")
                        .Replace(">", "")          // strip ELM prompt
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
            return line?.Trim() ?? string.Empty;
        }

        private void LogPidSupportFrom4100(string cleaned4100)
        {
            try
            {
                var m = Regex.Match(cleaned4100, @"4100([0-9A-F]{8})", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    Log("🧭 PID support: could not parse 4100 bitmap.");
                    return;
                }
                var mapHex = m.Groups[1].Value;
                if (!uint.TryParse(mapHex, NumberStyles.HexNumber, null, out var map))
                {
                    Log("🧭 PID support: invalid bitmap.");
                    return;
                }

                // Bit positions for 0x0C and 0x0D within the 4100 word:
                // MSB corresponds to PID 0x01; LSB corresponds to 0x20.
                bool supports010C = (map & (1u << (32 - 0x0C))) != 0; // RPM
                bool supports010D = (map & (1u << (32 - 0x0D))) != 0; // Speed
                Log($"🧭 PID support → 010C(RPM): {(supports010C ? "yes" : "no")}, 010D(Speed): {(supports010D ? "yes" : "no")} (0x{mapHex})");
            }
            catch (Exception ex)
            {
                Log($"🧭 PID support parse error: {ex.Message}");
            }
        }

        private async Task ApplyProfileHintsAsync(VehicleProfile? profile)
        {
            // Protocol hint → set, and remember if it's ISO vs CAN for timeout tuning
            var proto = profile?.ProtocolHint?.ToUpperInvariant() ?? "AUTO";
            _isIso9141 = false;

            switch (proto)
            {
                case "ISO9141":
                    await SendCommandAsync("ATSP3");    // ISO9141-2
                    _isIso9141 = true;
                    break;

                case "CAN11_500":
                    await SendCommandAsync("ATSP6");    // CAN 11/500
                    break;

                case "CAN29_500":
                    await SendCommandAsync("ATSP7");    // CAN 29/500
                    break;

                default:
                    await SendCommandAsync("ATSP0");    // AUTO
                    break;
            }

            // Throughput tunings (safe defaults; adjust per your adapter if needed)
            await SendCommandAsync("ATE0");     // echo off
            await SendCommandAsync("ATL0");     // linefeeds off
            await SendCommandAsync("ATS0");     // spaces off
            await SendCommandAsync("ATH0");     // headers off for normal polling
            await SendCommandAsync("ATAT2");    // adaptive timing 2 (faster)

            // Timeouts: shorter for CAN, longer for ISO9141
            if (_isIso9141)
            {
                await SendCommandAsync("ATST 20");   // ISO needs longer silence timeout
                _pidTimeoutMs = 1100;
            }
            else
            {
                await SendCommandAsync("ATST 0A");   // shorter timeout for CAN
                _pidTimeoutMs = 500;
            }

            // Optional init script lines (headers/filters etc.)
            if (profile?.InitScript is { Count: > 0 })
            {
                foreach (var line in profile.InitScript)
                    await SendCommandAsync(line);
            }

            // Optional CAN headers/filters (if provided in profile)
            if (!string.IsNullOrWhiteSpace(profile?.CanHeaderTx))
                await SendCommandAsync($"ATSH {profile.CanHeaderTx}");
            if (!string.IsNullOrWhiteSpace(profile?.CanHeaderRxFilter))
                await SendCommandAsync($"ATCRA {profile.CanHeaderRxFilter}");
        }

        private void Log(string message)
        {
            _log.Info(message);
            OnLog?.Invoke($"[ELM327] {message}");
        }

        public void Disconnect()
        {
            try
            {
                _transport.Disconnect();
                IsConnected = false;
                Log("🔌 Disconnected from ELM327.");
            }
            catch (Exception ex)
            {
                Log($"⚠️ Disconnect error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { _ioLock?.Dispose(); } catch { /* ignore */ }
            try { _transport?.Disconnect(); } catch { /* ignore */ }
        }
    }
}