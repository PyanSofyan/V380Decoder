using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class RtspServer
    {
        private readonly int port;
        private readonly bool secure;
        private readonly string username;
        private readonly string password;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        // concurrent set of active sessions
        private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
        private int nextId;

        // SPS/PPS from first keyframe – used for SDP fmtp line (H.264)
        private byte[] cachedSps, cachedPps;
        // VPS/SPS/PPS for H.265
        private byte[] cacheVps, cacheH265Sps, cacheH265Pps;
        // detected codec: false=H.264, true=H.265
        private bool isH265 = false;
        private readonly object sdpLock = new();

        public RtspServer(int port, bool secure, string username, string password)
        {
            this.port = port;
            this.username = username;
            this.password = password;
            this.secure = secure;
        }

        public bool IsSecure => secure;
        public string Username => username;
        public string Password => password;

        public void Start()
        {
            string basicAuth = secure ? $"{username}:{password}@" : string.Empty;
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(10);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
            acceptThread.Start();
            Console.Error.WriteLine($"[RTSP] rtsp://{basicAuth}{NetworkHelper.GetLocalIPAddress()}:{port}/live");
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var tcp = listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    int id = Interlocked.Increment(ref nextId);
                    var s = new RtspSession(id, tcp, this, secure);
                    sessions[id] = s;
                    s.Start();
                    s.OnClose += () => sessions.TryRemove(id, out _);
                }
                catch { }
            }
        }

        // Called from main receive loop for every complete video frame
        public void PushVideo(FrameData f)
        {
            if (f.RawType == 0x28 || f.RawType == 0x29)
            {
                // H.265 frames: cache VPS/SPS/PPS from first keyframe
                if (f.RawType == 0x28) CacheH265Params(f.Payload);
            }
            else
            {
                if (f.IsKeyframe) CacheSpsFromIdr(f.Payload);
            }
            foreach (var s in sessions.Values) s.PushVideo(f);
        }

        // Called from main receive loop for every complete audio frame
        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        // ── H.264 SPS/PPS extraction ───────────────────────────────────
        void CacheSpsFromIdr(byte[] data)
        {
            lock (sdpLock)
            {
                if (cachedSps != null && cachedPps != null) return; // already cached
                ParseNals(data, (nalType, nal) =>
                {
                    if (nalType == 7 && cachedSps == null) cachedSps = nal;
                    if (nalType == 8 && cachedPps == null) cachedPps = nal;
                });
            }
        }

        // ── H.265 VPS/SPS/PPS extraction ────────────────────────────────
        void CacheH265Params(byte[] data)
        {
            lock (sdpLock)
            {
                if (cacheVps != null && cacheH265Sps != null && cacheH265Pps != null) return;
                isH265 = true;
                int i = 0, len = data.Length;
                while (i < len)
                {
                    int sc = FindStartCode(data, i);
                    if (sc < 0) break;
                    int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
                    int nalStart = sc + scLen;
                    if (nalStart >= len) break;
                    int next = FindStartCode(data, nalStart);
                    int nalEnd = next < 0 ? len : next;
                    if (nalStart + 1 >= nalEnd) { i = nalEnd; continue; }
                    int nalType = (data[nalStart] >> 1) & 0x3F; // H.265 NAL type
                    var nal = new byte[nalEnd - nalStart];
                    Array.Copy(data, nalStart, nal, 0, nal.Length);
                    if (nalType == 32 && cacheVps == null) cacheVps = nal;      // VPS
                    else if (nalType == 33 && cacheH265Sps == null) cacheH265Sps = nal; // SPS
                    else if (nalType == 34 && cacheH265Pps == null) cacheH265Pps = nal; // PPS
                    i = nalEnd;
                }
            }
        }

        public bool IsH265 => isH265;

        // Walk H.264 Annex-B start codes, call cb(nalType, nalBytes) for each NAL
        internal static void ParseNals(byte[] data, Action<int, byte[]> cb)
        {
            int i = 0, len = data.Length;
            while (i < len)
            {
                // find start code
                int sc = FindStartCode(data, i);
                if (sc < 0) break;
                int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
                int nalStart = sc + scLen;
                if (nalStart >= len) break;
                // find next start code
                int next = FindStartCode(data, nalStart);
                int nalEnd = next < 0 ? len : next;
                int nalType = data[nalStart] & 0x1F;
                var nal = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nal, 0, nal.Length);
                cb(nalType, nal);
                i = nalEnd;
            }
        }

        internal static void ParseNalsH265(byte[] data, Action<int, byte[]> cb)
        {
            int i = 0, len = data.Length;
            while (i < len)
            {
                int sc = FindStartCode(data, i);
                if (sc < 0) break;
                int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
                int nalStart = sc + scLen;
                if (nalStart >= len) break;
                int next = FindStartCode(data, nalStart);
                int nalEnd = next < 0 ? len : next;
                if (nalStart + 1 >= nalEnd) { i = nalEnd; continue; }
                int nalType = (data[nalStart] >> 1) & 0x3F; // H.265 NAL type
                var nal = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nal, 0, nal.Length);
                cb(nalType, nal);
                i = nalEnd;
            }
        }

        static int FindStartCode(byte[] d, int from)
        {
            for (int i = from; i + 3 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0)
                {
                    if (d[i + 2] == 1) return i;
                    if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) return i;
                }
            }
            return -1;
        }

        public string BuildSdp()
        {
            lock (sdpLock)
            {
                if (isH265 && cacheVps != null && cacheH265Sps != null && cacheH265Pps != null)
                {
                    string vpsB64 = Convert.ToBase64String(cacheVps);
                    string spsB64 = Convert.ToBase64String(cacheH265Sps);
                    string ppsB64 = Convert.ToBase64String(cacheH265Pps);
                    return
                        "v=0\r\n" +
                        "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                        "s=V380 Live\r\n" +
                        "t=0 0\r\n" +
                        "a=recvonly\r\n" +
                        "m=video 0 RTP/AVP 96\r\n" +
                        "a=rtpmap:96 H265/90000\r\n" +
                        $"a=fmtp:96 packetization-mode=1;sprop-vps={vpsB64};sprop-sps={spsB64};sprop-pps={ppsB64}\r\n" +
                        "a=control:trackID=0\r\n" +
                        "m=audio 0 RTP/AVP 8\r\n" +
                        "a=rtpmap:8 PCMA/8000/1\r\n" +
                        "a=control:trackID=1\r\n";
                }

                string fmtp = "";
                if (cachedSps != null && cachedPps != null)
                {
                    string spsB64 = Convert.ToBase64String(cachedSps);
                    string ppsB64 = Convert.ToBase64String(cachedPps);
                    string pli = cachedSps.Length >= 4
                        ? $"{cachedSps[1]:X2}{cachedSps[2]:X2}{cachedSps[3]:X2}"
                        : "64001F";
                    fmtp = $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={spsB64},{ppsB64};profile-level-id={pli}\r\n";
                }
                return
                    "v=0\r\n" +
                    "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                    "s=V380 Live\r\n" +
                    "t=0 0\r\n" +
                    "a=recvonly\r\n" +
                    "m=video 0 RTP/AVP 96\r\n" +
                    "a=rtpmap:96 H264/90000\r\n" +
                    fmtp +
                    "a=control:trackID=0\r\n" +
                    "m=audio 0 RTP/AVP 8\r\n" +
                    "a=rtpmap:8 PCMA/8000/1\r\n" +
                    "a=control:trackID=1\r\n";
            }
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            foreach (var s in sessions.Values) s.Close();
        }
    }
}
