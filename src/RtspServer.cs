using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class RtspServer
    {
        private readonly int port;
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        // concurrent set of active sessions
        private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
        private int nextId;

        // SPS/PPS from first keyframe – used for SDP fmtp line
        private byte[] cachedSps, cachedPps;
        private readonly object sdpLock = new();

        public RtspServer(int port) { this.port = port; }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(10);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
            acceptThread.Start();
            Console.Error.WriteLine($"[RTSP] rtsp://{NetworkHelper.GetLocalIPAddress()}:{port}/live");
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
                    var s = new RtspSession(id, tcp, this);
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
            if (f.IsKeyframe) CacheSpsFromIdr(f.Payload);
            foreach (var s in sessions.Values) s.PushVideo(f);
        }

        // Called from main receive loop for every complete audio frame
        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        // ── SPS/PPS extraction ───────────────────────────────────
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
            string fmtp = "";
            lock (sdpLock)
            {
                if (cachedSps != null && cachedPps != null)
                {
                    string spsB64 = Convert.ToBase64String(cachedSps);
                    string ppsB64 = Convert.ToBase64String(cachedPps);
                    // profile-level-id = first 3 bytes of SPS (after NAL header)
                    string pli = cachedSps.Length >= 3
                        ? $"{cachedSps[0]:X2}{cachedSps[1]:X2}{cachedSps[2]:X2}"
                        : "64001F";
                    fmtp = $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={spsB64},{ppsB64};profile-level-id={pli}\r\n";
                }
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

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            foreach (var s in sessions.Values) s.Close();
        }
    }
}