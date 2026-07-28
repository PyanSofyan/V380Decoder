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

        // Codec parameter sets used in SDP.
        private byte[] cachedVps, cachedSps, cachedPps;
        private VideoCodec cachedCodec = VideoCodec.H264;
        private bool codecKnown;
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
            CacheParameterSets(f);
            foreach (var s in sessions.Values) s.PushVideo(f);
        }

        // Called from main receive loop for every complete audio frame
        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        // ── codec parameter set extraction ──────────────────────
        void CacheParameterSets(FrameData frame)
        {
            lock (sdpLock)
            {
                if (!codecKnown || cachedCodec != frame.Codec)
                {
                    cachedCodec = frame.Codec;
                    codecKnown = true;
                    cachedVps = null;
                    cachedSps = null;
                    cachedPps = null;
                    Console.Error.WriteLine($"[RTSP] video codec={cachedCodec}");
                }

                ParseNals(frame.Payload, frame.Codec, (nalType, nal) =>
                {
                    if (frame.Codec == VideoCodec.H264)
                    {
                        if (nalType == 7) cachedSps = nal;
                        if (nalType == 8) cachedPps = nal;
                        return;
                    }

                    if (nalType == 32) cachedVps = nal;
                    if (nalType == 33) cachedSps = nal;
                    if (nalType == 34) cachedPps = nal;
                });
            }
        }

        // Walk Annex-B start codes, call cb(nalType, nalBytes) for each NAL.
        internal static void ParseNals(
            byte[] data,
            VideoCodec codec,
            Action<int, byte[]> callback)
        {
            AnnexB.Parse(data, codec, callback);
        }

        public string BuildSdp()
        {
            string videoDescription;
            lock (sdpLock)
            {
                if (!codecKnown)
                {
                    videoDescription =
                        "m=video 0 RTP/AVP 96 97\r\n" +
                        "a=rtpmap:96 H264/90000\r\n" +
                        "a=rtpmap:97 H265/90000\r\n";
                }
                else if (cachedCodec == VideoCodec.H265)
                {
                    string fmtp = "";
                    if (cachedVps != null && cachedSps != null && cachedPps != null)
                    {
                        fmtp =
                            "a=fmtp:97 " +
                            $"sprop-vps={Convert.ToBase64String(cachedVps)};" +
                            $"sprop-sps={Convert.ToBase64String(cachedSps)};" +
                            $"sprop-pps={Convert.ToBase64String(cachedPps)}\r\n";
                    }

                    videoDescription =
                        "m=video 0 RTP/AVP 97\r\n" +
                        "a=rtpmap:97 H265/90000\r\n" +
                        fmtp;
                }
                else
                {
                    string fmtp = "";
                    if (cachedSps != null && cachedPps != null)
                    {
                        string profileLevelId = cachedSps.Length >= 4
                            ? $"{cachedSps[1]:X2}{cachedSps[2]:X2}{cachedSps[3]:X2}"
                            : "64001F";
                        fmtp =
                            "a=fmtp:96 packetization-mode=1;" +
                            $"sprop-parameter-sets={Convert.ToBase64String(cachedSps)}," +
                            $"{Convert.ToBase64String(cachedPps)};" +
                            $"profile-level-id={profileLevelId}\r\n";
                    }

                    videoDescription =
                        "m=video 0 RTP/AVP 96\r\n" +
                        "a=rtpmap:96 H264/90000\r\n" +
                        fmtp;
                }
            }

            return
                "v=0\r\n" +
                "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                "s=V380 Live\r\n" +
                "t=0 0\r\n" +
                "a=recvonly\r\n" +
                videoDescription +
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
