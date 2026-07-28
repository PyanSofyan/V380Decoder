using System.Diagnostics;
using H264Sharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace V380Decoder.src
{
    public class SnapshotManager : IDisposable
    {
        // ── shared state ──────────────────────────────────────────
        private readonly object _lock = new();
        private byte[] _cachedJpeg = null;
        private int _width, _height;
        private VideoCodec _codec = VideoCodec.H264;
        private bool _codecKnown;

        // ── mjpeg subscribers ─────────────────────────────────────
        private readonly List<Action<byte[]>> _subscribers = new();
        private readonly object _subLock = new();
        private readonly object _ffmpegLock = new();

        // ── decode pipeline ───────────────────────────────────────
        private readonly bool _useFFmpeg;
        private readonly CancellationTokenSource _cts = new();

        // H264Sharp path
        private H264Decoder _decoder;
        private bool _decoderReady = false;

        // FFmpeg path
        private Process _ffmpegProc;
        private Stream _ffmpegStdin;
        private VideoCodec? _ffmpegCodec;
        private bool _ffmpegAwaitingKeyframe = true;

        // Frame queue 
        private readonly System.Threading.Channels.Channel<(byte[] data, bool isIFrame)> _queue =
        System.Threading.Channels.Channel.CreateBounded<(byte[], bool)>(
        new System.Threading.Channels.BoundedChannelOptions(30)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        // H.264 SPS/PPS or H.265 VPS/SPS/PPS
        private byte[] _vps, _sps, _pps;

        private bool _mjpegActive = false;
        private byte[] _lastIFrame = null;
        private readonly SemaphoreSlim _snapshotSem = new(1, 1);
        private bool _reportedMissingHevcDecoder;

        public SnapshotManager()
        {
            _useFFmpeg = IsFFmpegAvailable();
            LogUtils.debug($"[SNAP] decoder={(_useFFmpeg ? "FFmpeg" : "H264Sharp")}");

            if (!_useFFmpeg)
                _decoder = new H264Decoder();

            Task.Run(() => DecodeLoop(_cts.Token));
        }

        // ── public API ────────────────────────────────────────────

        public void SetMjpegActive(bool active)
        {
            _mjpegActive = active;
            LogUtils.debug($"[SNAP] MJPEG {(active ? "enable" : "disabled")}");

            if (active && _useFFmpeg && _codecKnown)
                EnsureFFmpegPipe(_codec);
        }

        public void UpdateFrame(
            byte[] frame,
            int width,
            int height,
            bool isIFrame,
            VideoCodec codec)
        {
            bool codecChanged;
            lock (_lock)
            {
                _width = width;
                _height = height;
                codecChanged = !_codecKnown || _codec != codec;
                if (codecChanged)
                {
                    _codec = codec;
                    _codecKnown = true;
                    _vps = null;
                    _sps = null;
                    _pps = null;
                    _lastIFrame = null;
                    _cachedJpeg = null;
                }
            }

            if (codecChanged)
                LogUtils.debug($"[SNAP] codec={codec}");

            ExtractParameterSets(frame, codec);

            if (isIFrame)
            {
                lock (_lock) { _lastIFrame = (byte[])frame.Clone(); }
            }

            if (!_mjpegActive) return;

            if (_useFFmpeg)
            {
                try
                {
                    EnsureFFmpegPipe(codec);
                    lock (_ffmpegLock)
                    {
                        if (_ffmpegAwaitingKeyframe && !isIFrame)
                            return;

                        byte[] pipeFrame = isIFrame && HasParameterSets(codec)
                            ? PrependParameterSets(frame, codec)
                            : frame;

                        _ffmpegAwaitingKeyframe = false;
                        _ffmpegStdin?.Write(pipeFrame, 0, pipeFrame.Length);
                        _ffmpegStdin?.Flush();
                    }
                }
                catch (Exception ex) { LogUtils.debug($"[SNAP] FFmpeg write error: {ex.Message}"); }
            }
            else
            {
                if (codec == VideoCodec.H264)
                {
                    _queue.Writer.TryWrite(((byte[])frame.Clone(), isIFrame));
                }
                else if (!_reportedMissingHevcDecoder)
                {
                    Console.Error.WriteLine("[SNAP] FFmpeg is required to decode H.265 snapshots");
                    _reportedMissingHevcDecoder = true;
                }
            }
        }

        public byte[] GetSnapshot()
        {
            lock (_lock) { return _cachedJpeg; }
        }

        public async Task<byte[]> GetSnapshotAsync(int timeoutMs = 5000)
        {
            if (_mjpegActive)
            {
                lock (_lock) { return _cachedJpeg; }
            }

            if (!await _snapshotSem.WaitAsync(timeoutMs))
            {
                lock (_lock) { return _cachedJpeg; }
            }

            try
            {
                byte[] iFrame;
                VideoCodec codec;
                lock (_lock)
                {
                    iFrame = _lastIFrame;
                    codec = _codec;
                }

                if (iFrame == null || !HasParameterSets(codec))
                {
                    lock (_lock) { return _cachedJpeg; }
                }

                if (codec == VideoCodec.H265 && !_useFFmpeg)
                {
                    if (!_reportedMissingHevcDecoder)
                    {
                        Console.Error.WriteLine("[SNAP] FFmpeg is required to decode H.265 snapshots");
                        _reportedMissingHevcDecoder = true;
                    }
                    lock (_lock) { return _cachedJpeg; }
                }

                byte[] input = PrependParameterSets(iFrame, codec);

                byte[] jpeg = _useFFmpeg
                    ? await DecodeOneFrameFFmpeg(input, codec)
                    : DecodeH264Sharp(input, isIFrame: true);

                if (jpeg != null)
                    lock (_lock) { _cachedJpeg = jpeg; }

                lock (_lock) { return _cachedJpeg; }
            }
            finally
            {
                _snapshotSem.Release();
            }
        }

        public IDisposable Subscribe(Action<byte[]> callback)
        {
            lock (_subLock) _subscribers.Add(callback);
            return new Subscription(() => { lock (_subLock) _subscribers.Remove(callback); });
        }

        // ── decode loop ───────────────────────────────────────────

        private async Task DecodeLoop(CancellationToken ct)
        {
            if (_useFFmpeg)
                return;

            try
            {
                await foreach (var (data, isIFrame) in _queue.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        var jpeg = DecodeH264Sharp(data, isIFrame);
                        if (jpeg != null)
                        {
                            lock (_lock) { _cachedJpeg = jpeg; }
                            Notify(jpeg);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUtils.debug($"[SNAP] DecodeLoop error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ── H264Sharp ─────────────────────────────────────────────

        private byte[] DecodeH264Sharp(byte[] h264Data, bool isIFrame)
        {
            if (!_decoderReady)
            {
                _decoder.Initialize();
                _decoderReady = true;
            }

            int w, h;
            lock (_lock) { w = _width; h = _height; }

            byte[] input = (isIFrame && _sps != null && _pps != null)
                ? PrependParameterSets(h264Data, VideoCodec.H264)
                : h264Data;

            var rgb = new RgbImage(ImageFormat.Bgr, w, h);
            bool ok = _decoder.Decode(input, 0, input.Length, false, out DecodingState state, ref rgb);

            bool hasOutput = ok && (
                state == DecodingState.dsErrorFree ||
                state == DecodingState.dsDataErrorConcealed
            );

            if (!hasOutput)
            {
                LogUtils.debug($"[SNAP] H264Sharp skip: {state}");
                return null;
            }

            return ToJpeg(rgb.GetBytes(), w, h);
        }

        // ── FFmpeg persistent pipe ────────────────────────────────

        private void EnsureFFmpegPipe(VideoCodec codec)
        {
            lock (_ffmpegLock)
            {
                bool running = false;
                try { running = _ffmpegProc != null && !_ffmpegProc.HasExited; }
                catch { }

                if (running && _ffmpegCodec == codec && _ffmpegStdin != null)
                    return;

                StopFFmpegPipe();
                StartFFmpegPipe(codec);
            }
        }

        private void StartFFmpegPipe(VideoCodec codec)
        {
            string inputFormat = codec == VideoCodec.H265 ? "hevc" : "h264";
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -loglevel error " +
                            $"-f {inputFormat} -i pipe:0 " +
                            "-q:v 4 -f image2pipe -vcodec mjpeg pipe:1",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            _ffmpegProc = Process.Start(psi)!;
            _ffmpegStdin = _ffmpegProc.StandardInput.BaseStream;
            _ffmpegCodec = codec;
            _ffmpegAwaitingKeyframe = true;
            Stream output = _ffmpegProc.StandardOutput.BaseStream;

            Task.Run(() => ReadFFmpegOutput(output, _cts.Token));
            LogUtils.debug($"[SNAP] FFmpeg {inputFormat} pipe started");
        }

        private void StopFFmpegPipe()
        {
            try { _ffmpegStdin?.Close(); } catch { }
            try
            {
                if (_ffmpegProc != null && !_ffmpegProc.HasExited)
                    _ffmpegProc.WaitForExit(1000);
            }
            catch { }
            try
            {
                if (_ffmpegProc != null && !_ffmpegProc.HasExited)
                    _ffmpegProc.Kill();
            }
            catch { }
            _ffmpegProc?.Dispose();
            _ffmpegProc = null;
            _ffmpegStdin = null;
            _ffmpegCodec = null;
            _ffmpegAwaitingKeyframe = true;
        }

        private async Task<byte[]> DecodeOneFrameFFmpeg(byte[] videoData, VideoCodec codec)
        {
            try
            {
                string inputFormat = codec == VideoCodec.H265 ? "hevc" : "h264";
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-hide_banner -loglevel error " +
                                $"-f {inputFormat} -i pipe:0 " +
                                "-frames:v 1 -q:v 2 -f image2 -vcodec mjpeg pipe:1",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                await proc.StandardInput.BaseStream.WriteAsync(videoData);
                proc.StandardInput.Close();

                using var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms);
                await proc.WaitForExitAsync();

                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch (Exception ex)
            {
                LogUtils.debug($"[SNAP] Snapshot FFmpeg error: {ex.Message}");
                return null;
            }
        }

        private void ReadFFmpegOutput(Stream stdout, CancellationToken ct)
        {
            var buf = new List<byte>(256_000);
            var tmp = new byte[8192];

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int n = stdout.Read(tmp, 0, tmp.Length);
                    if (n == 0) break;

                    buf.AddRange(new ArraySegment<byte>(tmp, 0, n));

                    int start = FindBytes(buf, 0xFF, 0xD8);
                    if (start < 0) continue;

                    int end = FindBytes(buf, 0xFF, 0xD9, start + 2);
                    if (end < 0) continue;

                    int jpegLen = end + 2 - start;
                    var jpeg = buf.GetRange(start, jpegLen).ToArray();
                    buf.RemoveRange(0, end + 2);

                    if (jpeg.Length > 1000)
                    {
                        lock (_lock) { _cachedJpeg = jpeg; }
                        Notify(jpeg);
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtils.debug($"[SNAP] FFmpeg output error: {ex.Message}");
            }
        }

        private static int FindBytes(List<byte> buf, byte b0, byte b1, int from = 0)
        {
            for (int i = from; i < buf.Count - 1; i++)
                if (buf[i] == b0 && buf[i + 1] == b1) return i;
            return -1;
        }

        // ── helpers ───────────────────────────────────────────────

        private void ExtractParameterSets(byte[] data, VideoCodec codec)
        {
            AnnexB.Parse(data, codec, (nalType, nal) =>
            {
                lock (_lock)
                {
                    if (codec == VideoCodec.H264)
                    {
                        if (nalType == 7)
                        {
                            bool changed = _sps == null || !_sps.SequenceEqual(nal);
                            _sps = nal;
                            if (changed) LogUtils.debug($"[SNAP] H264 SPS {_sps.Length}b");
                        }
                        else if (nalType == 8)
                        {
                            bool changed = _pps == null || !_pps.SequenceEqual(nal);
                            _pps = nal;
                            if (changed) LogUtils.debug($"[SNAP] H264 PPS {_pps.Length}b");
                        }
                        return;
                    }

                    if (nalType == 32)
                    {
                        bool changed = _vps == null || !_vps.SequenceEqual(nal);
                        _vps = nal;
                        if (changed) LogUtils.debug($"[SNAP] H265 VPS {_vps.Length}b");
                    }
                    else if (nalType == 33)
                    {
                        bool changed = _sps == null || !_sps.SequenceEqual(nal);
                        _sps = nal;
                        if (changed) LogUtils.debug($"[SNAP] H265 SPS {_sps.Length}b");
                    }
                    else if (nalType == 34)
                    {
                        bool changed = _pps == null || !_pps.SequenceEqual(nal);
                        _pps = nal;
                        if (changed) LogUtils.debug($"[SNAP] H265 PPS {_pps.Length}b");
                    }
                }
            });
        }

        private bool HasParameterSets(VideoCodec codec)
        {
            lock (_lock)
            {
                return codec == VideoCodec.H265
                    ? _vps != null && _sps != null && _pps != null
                    : _sps != null && _pps != null;
            }
        }

        private byte[] PrependParameterSets(byte[] keyframe, VideoCodec codec)
        {
            byte[] sc = { 0x00, 0x00, 0x00, 0x01 };
            byte[] vps, sps, pps;
            lock (_lock)
            {
                vps = _vps;
                sps = _sps;
                pps = _pps;
            }

            using var ms = new MemoryStream();
            if (codec == VideoCodec.H265 && vps != null)
            {
                ms.Write(sc);
                ms.Write(vps);
            }
            if (sps != null)
            {
                ms.Write(sc);
                ms.Write(sps);
            }
            if (pps != null)
            {
                ms.Write(sc);
                ms.Write(pps);
            }
            ms.Write(keyframe);
            return ms.ToArray();
        }

        private static byte[] ToJpeg(byte[] bgr, int w, int h)
        {
            using var img = Image.LoadPixelData<Rgb24>(bgr, w, h);
            using var ms = new MemoryStream();
            img.Save(ms, new JpegEncoder { Quality = 80 });
            return ms.ToArray();
        }

        private void Notify(byte[] jpeg)
        {
            List<Action<byte[]>> subs;
            lock (_subLock) subs = new(_subscribers);
            foreach (var s in subs) try { s(jpeg); } catch { }
        }

        private bool IsFFmpegAvailable()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                bool exited = process.WaitForExit(2000);
                return exited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // ── dispose ───────────────────────────────────────────────

        public void Dispose()
        {
            _cts.Cancel();
            _queue.Writer.Complete();

            lock (_ffmpegLock)
                StopFFmpegPipe();

            _decoder?.Dispose();
        }

        private class Subscription(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
