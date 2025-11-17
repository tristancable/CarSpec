using CarSpec.Interfaces;
using CarSpec.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CarSpec.Services.Telemetry
{
    public sealed class ReplayService
    {
        private readonly IAppStorage _storage;
        private CancellationTokenSource? _cts;

        public bool IsReplaying => _cts != null;

        public ReplayService(IAppStorage storage) => _storage = storage;

        public async Task StartAsync(string id, Func<CarData, Task> onFrame, Action? onStop = null, double speed = 1.0)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var blob = await _storage.GetAsync<byte[]>($"rec.gz:{id}");
            if (blob is null) { onStop?.Invoke(); _cts = null; return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var src = new MemoryStream(blob);
                    Stream s;
                    try
                    {
                        s = new GZipStream(src, CompressionMode.Decompress);
                    }
                    catch
                    {
                        // Not gzipped – read raw
                        s = new MemoryStream(blob);
                    }

                    using var r = new StreamReader(s, Encoding.UTF8);

                    // header (first line)
                    _ = await r.ReadLineAsync();

                    long t0 = -1;
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await r.ReadLineAsync();
                        if (line == null) break;
                        if (line.Length == 0 || line[0] != '{') continue;

                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("t", out var tProp)) continue;
                        var t = tProp.GetInt64();

                        if (t0 < 0) t0 = NowMs();
                        var elapsed = NowMs() - t0;
                        var target = (long)(t / Math.Max(0.1, speed));
                        if (target > elapsed)
                            await Task.Delay((int)Math.Min(500, target - elapsed), ct);

                        var cd = new CarData
                        {
                            RPM = ReadInt(root, "rpm"),     // int default 0
                            Speed = ReadDouble(root, "spd"),   // double default 0
                            ThrottlePercent = ReadDouble(root, "thr"),
                            CoolantTempF = ReadDouble(root, "ct"),
                            IntakeTempF = ReadDouble(root, "iat"),
                            EngineLoadPercent = ReadDouble(root, "load"),
                            MafGramsPerSec = ReadDouble(root, "maf"),
                            MapKpa = ReadDouble(root, "map"),
                            BaroKPa = ReadInt(root, "baro"),
                            FuelLevelPercent = ReadDouble(root, "fuel"),
                            LastUpdated = DateTime.Now
                        };

                        await onFrame(cd);
                    }
                }
                catch
                {
                    // swallow to keep UI resilient
                }
                finally { onStop?.Invoke(); }
            }, ct);
        }

        public void Stop() { try { _cts?.Cancel(); } catch { } _cts = null; }

        private static long NowMs() =>
            (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

        // NEW helpers: safe, non-nullable reads with defaults
        private static double ReadDouble(JsonElement root, string name, double def = 0d)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
               ? v.GetDouble()
               : def;

        private static int ReadInt(JsonElement root, string name, int def = 0)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
               ? (int)Math.Round(v.GetDouble())
               : def;
    }
}