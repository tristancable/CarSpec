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

        // Pause state
        private volatile bool _paused;
        private long _pauseStartedMs;
        private long _totalPausedMs;
        private double _speed = 1.0;

        public bool IsReplaying => _cts != null;
        public bool IsPaused => _paused;

        public ReplayService(IAppStorage storage) => _storage = storage;

        public async Task StartAsync(string id, Func<CarData, Task> onFrame, Action? onStop = null, double speed = 1.0)
        {
            // kill any existing replay
            Stop();

            _speed = speed <= 0 ? 1.0 : speed;
            _paused = false;
            _pauseStartedMs = 0;
            _totalPausedMs = 0;

            var blob = await _storage.GetAsync<byte[]>($"rec.gz:{id}");
            if (blob is null)
            {
                onStop?.Invoke();
                return;
            }

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

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
                        // Not gzipped – treat as raw
                        s = new MemoryStream(blob);
                    }

                    using var r = new StreamReader(s, Encoding.UTF8);

                    // header line (ignored)
                    _ = await r.ReadLineAsync();

                    long startRealMs = -1;

                    while (!ct.IsCancellationRequested)
                    {
                        // Handle pause: freeze time until resumed
                        if (_paused)
                        {
                            if (_pauseStartedMs == 0)
                                _pauseStartedMs = NowMs();

                            while (_paused && !ct.IsCancellationRequested)
                            {
                                try { await Task.Delay(50, ct); } catch { }
                            }

                            if (ct.IsCancellationRequested)
                                break;

                            if (_pauseStartedMs != 0)
                            {
                                _totalPausedMs += NowMs() - _pauseStartedMs;
                                _pauseStartedMs = 0;
                            }
                        }

                        var line = await r.ReadLineAsync();
                        if (line == null)
                        {
                            // EOF → finished replay
                            break;
                        }

                        if (line.Length == 0 || line[0] != '{')
                            continue;

                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (!root.TryGetProperty("t", out var tProp))
                            continue;

                        var t = tProp.GetInt64(); // ms since start of recording

                        if (startRealMs < 0)
                            startRealMs = NowMs();

                        // "Active" playback time = real time - start - paused segments
                        var elapsedActive = NowMs() - startRealMs - _totalPausedMs;
                        var targetMs = (long)(t / _speed);

                        if (targetMs > elapsedActive)
                        {
                            var delayMs = (int)Math.Min(500, targetMs - elapsedActive);
                            try { await Task.Delay(delayMs, ct); } catch { }
                            if (ct.IsCancellationRequested) break;
                        }

                        var cd = new CarData
                        {
                            RPM = ReadInt(root, "rpm"),
                            Speed = ReadDouble(root, "spd"),
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
                finally
                {
                    onStop?.Invoke();
                }
            }, ct);
        }

        public void Pause()
        {
            if (_cts == null) return;
            _paused = true;
        }

        public void Resume()
        {
            if (_cts == null) return;
            _paused = false;
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            _paused = false;
            _pauseStartedMs = 0;
            _totalPausedMs = 0;
        }

        private static long NowMs() =>
            (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

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