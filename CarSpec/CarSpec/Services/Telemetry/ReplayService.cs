using CarSpec.Interfaces;
using CarSpec.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CarSpec.Services.Telemetry
{
    public sealed class ReplayService
    {
        private readonly IAppStorage _storage;
        private CancellationTokenSource? _cts;

        private volatile bool _paused;
        private long _pauseStartedMs;
        private long _totalPausedMs;
        private double _speed = 1.0;
        private volatile bool _suppressOnStop;

        private string? _currentId;
        private Func<CarData, Task>? _currentOnFrame;
        private Action? _currentOnStop;
        private long _currentT;
        private int _replayGeneration;

        public bool IsReplaying => _cts != null;
        public bool IsPaused => _paused;

        public ReplayService(IAppStorage storage) => _storage = storage;

        public async Task StartAsync(string id, Func<CarData, Task> onFrame, Action? onStop = null, double speed = 1.0, long startOffsetMs = 0)
        {
            Stop();

            var myGeneration = Interlocked.Increment(ref _replayGeneration);

            _speed = speed <= 0 ? 1.0 : speed;
            _paused = false;
            _pauseStartedMs = 0;
            _totalPausedMs = 0;

            _currentId = id;
            _currentOnFrame = onFrame;
            _currentOnStop = onStop;
            _currentT = 0;

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
                        s = new MemoryStream(blob);
                    }

                    using var r = new StreamReader(s, Encoding.UTF8);

                    _ = await r.ReadLineAsync();

                    long startRealMs = -1;
                    bool skipping = startOffsetMs > 0;

                    while (!ct.IsCancellationRequested)
                    {
                        if (myGeneration != _replayGeneration)
                            break;

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
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                            continue;

                        JsonDocument doc;
                        try
                        {
                            doc = JsonDocument.Parse(line);
                        }
                        catch
                        {
                            continue;
                        }

                        using (doc)
                        {
                            var root = doc.RootElement;

                            if (!root.TryGetProperty("t", out var tProp) ||
                                !root.TryGetProperty("data", out var dataProp) ||
                                tProp.ValueKind != JsonValueKind.Number ||
                                dataProp.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var t = tProp.GetInt64();
                            var data = dataProp;

                            if (skipping)
                            {
                                if (t < startOffsetMs)
                                    continue;

                                skipping = false;
                                startRealMs = NowMs() - (long)(t / _speed);
                            }

                            if (startRealMs < 0)
                                startRealMs = NowMs();

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
                                Speed = ReadDouble(data, "speed"),
                                RPM = ReadDouble(data, "rpm"),
                                ThrottlePercent = ReadDouble(data, "throttlePercent"),
                                FuelLevelPercent = ReadDouble(data, "fuelLevelPercent"),
                                OilTempF = ReadDouble(data, "oilTempF"),
                                CoolantTempF = ReadDouble(data, "coolantTempF"),
                                IntakeTempF = ReadDouble(data, "intakeTempF"),
                                EngineLoadPercent = ReadDouble(data, "engineLoadPercent"),
                                MapKpa = ReadDouble(data, "mapKpa"),
                                // MapPsi is in the JSON too, but only needed if you use it:
                                // MapPsi               = ReadDouble(data, "mapPsi"),
                                TimingAdvanceDeg = ReadDouble(data, "timingAdvanceDeg"),
                                MafGramsPerSec = ReadDouble(data, "mafGramsPerSec"),
                                DistanceWithMilKm = ReadInt(data, "distanceWithMilKm"),
                                DistanceSinceClearKm = ReadInt(data, "distanceSinceClearKm"),
                                WarmUpsSinceClear = ReadInt(data, "warmUpsSinceClear"),
                                FuelRailPressureRelKPa = ReadDouble(data, "fuelRailPressureRelKPa"),
                                FuelRailGaugePressureKPa = ReadInt(data, "fuelRailGaugePressureKPa"),
                                CommandedEgrPercent = ReadDouble(data, "commandedEgrPercent"),
                                EgrErrorPercent = ReadDouble(data, "egrErrorPercent"),
                                CommandedEvapPurgePercent = ReadDouble(data, "commandedEvapPurgePercent"),
                                EvapVaporPressurePa = ReadInt(data, "evapVaporPressurePa"),
                                BaroKPa = ReadInt(data, "baroKPa"),
                                LastUpdated = DateTime.Now
                            };

                            _currentT = t;

                            if (myGeneration != _replayGeneration)
                                break;

                            await onFrame(cd);
                        }
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    var suppress = _suppressOnStop;
                    _suppressOnStop = false;

                    _cts = null;
                    _paused = false;
                    _pauseStartedMs = 0;
                    _totalPausedMs = 0;

                    if (!suppress)
                    {
                        onStop?.Invoke();
                    }
                }
            }, ct);
        }

        public async Task RewindAsync(int seconds)
        {
            if (_currentId is null || _currentOnFrame is null)
                return;

            var delta = Math.Max(1, seconds);
            long target = _currentT - delta * 1000L;
            if (target < 0) target = 0;

            var wasPaused = _paused;

            StopInternal(suppressCallback: true);

            await StartAsync(_currentId, _currentOnFrame, _currentOnStop, _speed, target);

            if (wasPaused)
            {
                await Task.Delay(80);
                Pause();
            }
        }

        public async Task FastForwardAsync(int seconds, long? durationMs = null)
        {
            if (_currentId is null || _currentOnFrame is null)
                return;

            var delta = Math.Max(1, seconds);
            long target = _currentT + delta * 1000L;

            if (durationMs is { } dur && target > dur)
                target = dur;

            var wasPaused = _paused;

            StopInternal(suppressCallback: true);

            await StartAsync(_currentId, _currentOnFrame, _currentOnStop, _speed, target);

            if (wasPaused)
            {
                await Task.Delay(80);
                Pause();
            }
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

        private void StopInternal(bool suppressCallback)
        {
            if (suppressCallback)
                _suppressOnStop = true;

            try { _cts?.Cancel(); } catch { }

            _cts = null;
            _paused = false;
            _pauseStartedMs = 0;
            _totalPausedMs = 0;
        }

        public void Stop()
        {
            StopInternal(suppressCallback: false);
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