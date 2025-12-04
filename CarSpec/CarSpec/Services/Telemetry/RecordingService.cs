using CarSpec.Interfaces;
using CarSpec.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CarSpec.Services.Telemetry
{
    public sealed class RecordingService
    {
        private readonly IAppStorage _storage;
        private StringBuilder? _buffer;
        private RecordingMeta? _meta;
        private long _t0;
        private long _lastT;
        private int _frames;

        private CarData? _lastSnap;
        private long _lastFrameT;
        private const int FrameIntervalMs = 200;

        public bool IsRecording => _buffer != null;
        private const string IndexKey = "rec.index";
        private static string DataKey(string id) => $"rec.gz:{id}";

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public RecordingService(IAppStorage storage) => _storage = storage;

        public Task<string> StartAsync(VehicleProfile? profile, EcuFingerprint? fp, string? note = null)
        {
            if (IsRecording) throw new InvalidOperationException("Already recording.");

            _buffer = new StringBuilder(capacity: 32 * 1024);
            _frames = 0;
            _lastT = 0;
            _lastFrameT = 0;
            _lastSnap = null;
            _t0 = NowMs();

            _meta = new RecordingMeta
            {
                VehicleId = profile?.Id,
                Vehicle = profile is null ? null : $"{profile.Year} {profile.Make} {profile.Model}",
                Vin = fp?.Vin ?? profile?.LastKnownVin ?? profile?.VinLast,
                Protocol = fp?.Protocol ?? profile?.ProtocolDetected,
                Notes = note
            };

            var header = new
            {
                schema = "carspec.rec.v1",
                createdUtc = _meta.CreatedUtc,
                vehicleId = _meta.VehicleId,
                vehicle = _meta.Vehicle,
                vin = _meta.Vin,
                protocol = _meta.Protocol,
                notes = _meta.Notes
            };
            _buffer.AppendLine(JsonSerializer.Serialize(header, JsonOpts));

            return Task.FromResult(_meta.Id);
        }

        public Task AppendAsync(CarData snap)
        {
            if (!IsRecording || _buffer is null) return Task.CompletedTask;

            var nowT = NowMs() - _t0;

            if (_lastSnap is not null && _lastFrameT > 0)
            {
                while (_lastFrameT + FrameIntervalMs <= nowT)
                {
                    _lastFrameT += FrameIntervalMs;
                    AppendFrame(_lastFrameT, _lastSnap);
                }
            }

            _lastT = nowT;
            _lastFrameT = nowT;
            _lastSnap = snap.Clone();

            AppendFrame(nowT, snap);

            return Task.CompletedTask;
        }

        private void AppendFrame(long t, CarData snap)
        {
            var frame = new CarRecordingFrame
            {
                t = t,
                data = snap.Clone()
            };

            _buffer!.AppendLine(JsonSerializer.Serialize(frame, JsonOpts));
            _frames++;
        }

        public async Task<RecordingMeta?> StopAsync(bool gzip = true)
        {
            if (!IsRecording || _buffer is null || _meta is null) return _meta;

            _meta.Frames = _frames;
            _meta.DurationMs = _lastFrameT;

            var utf8 = Encoding.UTF8.GetBytes(_buffer.ToString());
            byte[] payload;
            if (gzip)
            {
                using var ms = new MemoryStream();
                using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    gz.Write(utf8, 0, utf8.Length);
                    gz.Flush();
                }
                payload = ms.ToArray();
            }
            else
            {
                payload = utf8;
            }

            _meta.ByteSize = payload.LongLength;

            await _storage.SetAsync(DataKey(_meta.Id), payload);

            var idx = await _storage.GetAsync<List<RecordingMeta>>(IndexKey) ?? new List<RecordingMeta>();
            var i = idx.FindIndex(x => x.Id == _meta.Id);
            if (i >= 0) idx[i] = _meta; else idx.Add(_meta);
            await _storage.SetAsync(IndexKey, idx);

            _buffer = null;
            var done = _meta;
            _meta = null;
            return done;
        }

        public async Task<string?> DumpRecordingAsync(string id)
        {
            var blob = await _storage.GetAsync<byte[]>(DataKey(id));
            if (blob is null || blob.Length == 0)
                return null;

            try
            {
                using var src = new MemoryStream(blob);
                using var gz = new GZipStream(src, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            catch
            {
                return Encoding.UTF8.GetString(blob);
            }
        }

        public async Task DeleteAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;

            await _storage.RemoveAsync(DataKey(id));

            var idx = await _storage.GetAsync<List<RecordingMeta>>(IndexKey)
                      ?? new List<RecordingMeta>();

            var removed = idx.RemoveAll(r => string.Equals(r.Id, id, StringComparison.Ordinal));

            if (removed > 0)
            {
                await _storage.SetAsync(IndexKey, idx);
            }
        }

        public async Task<List<RecordingMeta>> ListAsync()
            => await _storage.GetAsync<List<RecordingMeta>>(IndexKey) ?? new List<RecordingMeta>();

        public async Task<byte[]?> GetPayloadAsync(string id)
            => await _storage.GetAsync<byte[]>(DataKey(id));

        private static long NowMs() =>
            (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }
}