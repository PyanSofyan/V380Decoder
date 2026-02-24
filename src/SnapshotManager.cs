using H264Sharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace V380Decoder.src
{

    public class SnapshotManager : IDisposable
    {
        private byte[] spsNal = null;
        private byte[] ppsNal = null;
        private byte[] lastH264Frame = null;
        private byte[] cachedJpeg = null;
        private DateTime lastDecodeTime = DateTime.MinValue;
        private readonly object lockObj = new object();
        private bool isDecoding = false;
        private int imageWidth;
        private int imageHeight;
        private H264Decoder decoder = null;
        private bool decoderInitialized = false;
        private bool autoDecodeEnabled = true;
        private DateTime lastAutoDecodeTime = DateTime.MinValue;
        private const int MinAutoDecodeIntervalMs = 2000;

        public SnapshotManager()
        {
            decoder = new H264Decoder();
        }

        public void UpdateFrame(byte[] h264Frame, int width, int height)
        {
            lock (lockObj)
            {
                imageWidth = width;
                imageHeight = height;

                if (spsNal == null || ppsNal == null)
                {
                    ExtractSPSandPPS(h264Frame);
                }

                lastH264Frame = (byte[])h264Frame.Clone();
                cachedJpeg = null;
            }

            if (autoDecodeEnabled)
            {
                lock (lockObj)
                {
                    if ((DateTime.Now - lastAutoDecodeTime).TotalMilliseconds < MinAutoDecodeIntervalMs)
                        return;

                    if (isDecoding)
                        return;

                    var h264Data = PrependSPSandPPS(lastH264Frame);
                    isDecoding = true;
                    lastAutoDecodeTime = DateTime.Now;

                    ThreadPool.QueueUserWorkItem(_ => DecodeSnapshot(h264Data));
                }
            }
        }

        private void ExtractSPSandPPS(byte[] h264Data)
        {
            var nalUnits = FindNalUnits(h264Data);

            foreach (var nal in nalUnits)
            {
                if (nal.Length == 0) continue;

                int nalType = nal[0] & 0x1F;

                if (nalType == 7) // SPS
                {
                    spsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found SPS: {spsNal.Length} bytes");
                }
                else if (nalType == 8) // PPS
                {
                    ppsNal = (byte[])nal.Clone();
                    LogUtils.debug($"[SNAPSHOT] Found PPS: {ppsNal.Length} bytes");
                }
            }
        }

        private List<byte[]> FindNalUnits(byte[] h264Data)
        {
            var nalUnits = new List<byte[]>();
            int i = 0;

            while (i < h264Data.Length - 4)
            {
                if (h264Data[i] == 0 && h264Data[i + 1] == 0 &&
                    h264Data[i + 2] == 0 && h264Data[i + 3] == 1)
                {
                    int start = i + 4;
                    int end = start;

                    while (end < h264Data.Length - 4)
                    {
                        if (h264Data[end] == 0 && h264Data[end + 1] == 0 &&
                            h264Data[end + 2] == 0 && h264Data[end + 3] == 1)
                        {
                            break;
                        }
                        end++;
                    }

                    if (end == h264Data.Length - 4)
                        end = h264Data.Length;

                    byte[] nal = new byte[end - start];
                    Array.Copy(h264Data, start, nal, 0, nal.Length);
                    nalUnits.Add(nal);

                    i = end;
                }
                else
                {
                    i++;
                }
            }

            return nalUnits;
        }

        public byte[] GetSnapshot(int timeoutMs = 5000)
        {
            byte[] h264Data;
            bool needDecode;

            lock (lockObj)
            {
                if (cachedJpeg != null &&
                    (DateTime.Now - lastDecodeTime).TotalSeconds < 5)
                {
                    return cachedJpeg;
                }

                if (lastH264Frame == null || spsNal == null || ppsNal == null)
                {
                    LogUtils.debug("[SNAPSHOT] Missing frame or SPS/PPS");
                    return null;
                }

                if (isDecoding)
                    return null;

                h264Data = PrependSPSandPPS(lastH264Frame);
                needDecode = true;
                isDecoding = true;
            }

            if (needDecode)
            {
                DecodeSnapshot(h264Data);
            }

            lock (lockObj)
            {
                return cachedJpeg;
            }
        }

        private byte[] PrependSPSandPPS(byte[] idrFrame)
        {
            byte[] startCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

            using var ms = new MemoryStream();
            ms.Write(startCode, 0, 4);
            ms.Write(spsNal, 0, spsNal.Length);
            ms.Write(startCode, 0, 4);
            ms.Write(ppsNal, 0, ppsNal.Length);
            ms.Write(idrFrame, 0, idrFrame.Length);

            return ms.ToArray();
        }

        private void DecodeSnapshot(byte[] h264Data)
        {
            try
            {
                LogUtils.debug($"[SNAPSHOT] Decoding {h264Data.Length} bytes");
                if (!decoderInitialized)
                {
                    decoder.Initialize();
                    decoderInitialized = true;
                    LogUtils.debug("[SNAPSHOT] Decoder initialized");
                }

                RgbImage rgbOut = new(ImageFormat.Bgr, imageWidth, imageHeight);

                var decodedFrame = decoder.Decode(
                    h264Data,
                    0,
                    h264Data.Length,
                    false,
                    out DecodingState state,
                    ref rgbOut
                );

                if (!decodedFrame || state != DecodingState.dsErrorFree)
                {
                    LogUtils.debug($"[SNAPSHOT] Decode failed: {state}");

                    if (state == DecodingState.dsInitialOptExpected)
                    {
                        LogUtils.debug("[SNAPSHOT] Resetting decoder...");
                        decoder.Dispose();
                        decoder = new H264Decoder();
                        decoder.Initialize();
                        decoderInitialized = true;

                        // Retry decode
                        decodedFrame = decoder.Decode(
                            h264Data, 0, h264Data.Length,
                            false, out state, ref rgbOut
                        );

                        if (!decodedFrame)
                        {
                            LogUtils.debug($"[SNAPSHOT] Retry failed: {state}");
                            lock (lockObj) { isDecoding = false; }
                            return;
                        }
                    }
                    else
                    {
                        lock (lockObj) { isDecoding = false; }
                        return;
                    }
                }

                var jpeg = ConvertToJpeg(
                    rgbOut.GetBytes(),
                    imageWidth,
                    imageHeight
                );

                lock (lockObj)
                {
                    cachedJpeg = jpeg;
                    lastDecodeTime = DateTime.Now;
                    isDecoding = false;
                }

                autoDecodeEnabled = false;
                LogUtils.debug($"[SNAPSHOT] Success: {jpeg.Length} bytes JPEG");
            }
            catch (Exception ex)
            {
                LogUtils.debug($"[SNAPSHOT] Error: {ex.Message}");
                LogUtils.debug($"[SNAPSHOT] Stack: {ex.StackTrace}");
                lock (lockObj) { isDecoding = false; }
            }
        }

        private byte[] ConvertToJpeg(byte[] rgb, int width, int height)
        {
            using var image = Image.LoadPixelData<Rgb24>(rgb, width, height);
            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 80 });
            return ms.ToArray();
        }

        public void Dispose()
        {
            decoder?.Dispose();
            lock (lockObj)
            {
                lastH264Frame = null;
                cachedJpeg = null;
                spsNal = null;
                ppsNal = null;
            }
        }
    }
}