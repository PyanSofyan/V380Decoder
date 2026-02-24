using System.Net.Sockets;
using System.Text;

namespace V380Decoder.src
{
    public class RtspSession
    {
        private readonly int id;
        private readonly TcpClient tcp;
        private readonly NetworkStream ns;
        private readonly RtspServer server;
        private Thread readThread;
        private volatile bool playing;
        private volatile bool alive = true;

        // interleaved channels negotiated in SETUP
        private byte videoCh = 0, audioCh = 2;

        // RTP state
        private ushort videoSeq, audioSeq;
        private uint videoSsrc = (uint)new Random().Next();
        private uint audioSsrc = (uint)new Random().Next();

        public event Action OnClose;

        public RtspSession(int id, TcpClient tcp, RtspServer server)
        {
            this.id = id; this.tcp = tcp; this.server = server;
            ns = tcp.GetStream();
        }

        public void Start()
        {
            readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"rtsp-{id}" };
            readThread.Start();
        }

        public void Close()
        {
            alive = false;
            playing = false;
            try { tcp.Close(); } catch { }
            OnClose?.Invoke();
        }

        // ── RTSP request reader ──────────────────────────────────
        void ReadLoop()
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            try
            {
                while (alive)
                {
                    int n = ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    string raw = sb.ToString();
                    int end;
                    while ((end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        string req = raw[..(end + 4)];
                        raw = raw[(end + 4)..];
                        HandleRequest(req);
                    }
                    sb.Clear(); sb.Append(raw);
                }
            }
            catch { }
            finally { Close(); }
        }

        void HandleRequest(string req)
        {
            string[] lines = req.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0) return;

            string method = lines[0].Split(' ')[0];
            string url = lines[0].Split(' ').ElementAtOrDefault(1) ?? "";
            string cseq = lines.FirstOrDefault(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                                  ?.Split(':', 2)[1].Trim() ?? "0";
            string transport = lines.FirstOrDefault(l => l.StartsWith("Transport:", StringComparison.OrdinalIgnoreCase)) ?? "";

            switch (method)
            {
                case "OPTIONS":
                    Reply(cseq, "Public: OPTIONS,DESCRIBE,SETUP,PLAY,TEARDOWN");
                    break;

                case "DESCRIBE":
                    {
                        string sdp = server.BuildSdp();
                        byte[] body = Encoding.ASCII.GetBytes(sdp);
                        Send($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n" +
                             $"Content-Type: application/sdp\r\nContent-Length: {body.Length}\r\n\r\n{sdp}");
                        break;
                    }

                case "SETUP":
                    {
                        bool isAudio = url.Contains("trackID=1");
                        // Parse interleaved channels from client Transport header
                        // e.g. Transport: RTP/AVP/TCP;unicast;interleaved=0-1
                        byte ch = (byte)(isAudio ? 2 : 0);
                        var m = System.Text.RegularExpressions.Regex.Match(transport, @"interleaved=(\d+)-(\d+)");
                        if (m.Success) ch = byte.Parse(m.Groups[1].Value);

                        Reply(cseq,
                            $"Transport: RTP/AVP/TCP;unicast;interleaved={ch}-{ch + 1}",
                            "Session: 1");
                        break;
                    }

                case "PLAY":
                    Reply(cseq,
                        "Session: 1",
                        $"RTP-Info: url={url}/trackID=0;seq={videoSeq},url={url}/trackID=1;seq={audioSeq}");
                    playing = true;
                    Console.Error.WriteLine($"[RTSP#{id}] playing");
                    break;

                case "TEARDOWN":
                    Reply(cseq, "Session: 1");
                    Close();
                    break;

                default:
                    Send($"RTSP/1.0 501 Not Implemented\r\nCSeq: {cseq}\r\n\r\n");
                    break;
            }
        }

        void Reply(string cseq, params string[] headers)
        {
            var sb = new StringBuilder();
            sb.Append($"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\n");
            foreach (var h in headers) sb.Append(h + "\r\n");
            sb.Append("\r\n");
            Send(sb.ToString());
        }

        void Send(string s)
        {
            try
            {
                byte[] b = Encoding.ASCII.GetBytes(s);
                lock (ns) { ns.Write(b, 0, b.Length); ns.Flush(); }
            }
            catch { alive = false; }
        }

        // ── RTP video push  (H.264 Annex-B → RTP NAL/FU-A) ──────
        public void PushVideo(FrameData f)
        {
            if (!playing) return;

            // RTP timestamp: 90000 Hz, camera timestamp in milliseconds
            uint rts = (uint)(f.Timestamp * 90);

            RtspServer.ParseNals(f.Payload, (nalType, nal) =>
            {
                const int MTU = 1400;
                if (nal.Length <= MTU)
                {
                    // Single NAL unit packet
                    SendRtp(videoCh, 96, videoSeq++, rts, videoSsrc, nal, 0, nal.Length, marker: true);
                }
                else
                {
                    // FU-A fragmentation
                    byte nalHdr = nal[0];
                    byte fuInd = (byte)((nalHdr & 0xE0) | 28); // NRI from original, type=28
                    int offset = 1; // skip original NAL header
                    bool first = true;

                    while (offset < nal.Length)
                    {
                        int chunk = Math.Min(MTU - 2, nal.Length - offset);
                        bool last = offset + chunk >= nal.Length;

                        byte fuHdr = (byte)(nalHdr & 0x1F);              // NAL type
                        if (first) fuHdr |= 0x80;                        // S bit
                        if (last) fuHdr |= 0x40;                        // E bit

                        var frag = new byte[2 + chunk];
                        frag[0] = fuInd;
                        frag[1] = fuHdr;
                        Array.Copy(nal, offset, frag, 2, chunk);

                        SendRtp(videoCh, 96, videoSeq++, rts, videoSsrc,
                                frag, 0, frag.Length, marker: last);
                        offset += chunk;
                        first = false;
                    }
                }
            });
        }

        // ── RTP audio push  (PCMA raw samples) ──────────────────
        public void PushAudio(FrameData f)
        {
            if (!playing) return;

            // RTP timestamp: 8000 Hz, camera timestamp in milliseconds
            uint rts = (uint)(f.Timestamp * 8);

            // Send in 20 ms chunks = 160 samples at 8 kHz
            const int CHUNK = 160;
            for (int off = 0; off < f.Payload.Length; off += CHUNK)
            {
                int len = Math.Min(CHUNK, f.Payload.Length - off);
                SendRtp(audioCh, 8, audioSeq++, rts, audioSsrc,
                        f.Payload, off, len, marker: false);
                rts += (uint)len; // advance timestamp by samples sent
            }
        }

        // ── Low-level RTP sender with RTSP interleaved framing ───
        // RFC 2326 §10.12:  $ | channel (1B) | length (2B BE) | RTP packet
        void SendRtp(byte channel, byte pt, ushort seq, uint ts, uint ssrc,
                     byte[] payload, int offset, int length, bool marker)
        {
            var rtp = new byte[12 + length];
            rtp[0] = 0x80;
            rtp[1] = (byte)((marker ? 0x80 : 0) | (pt & 0x7F));
            rtp[2] = (byte)(seq >> 8);
            rtp[3] = (byte)seq;
            rtp[4] = (byte)(ts >> 24); rtp[5] = (byte)(ts >> 16);
            rtp[6] = (byte)(ts >> 8); rtp[7] = (byte)ts;
            rtp[8] = (byte)(ssrc >> 24); rtp[9] = (byte)(ssrc >> 16);
            rtp[10] = (byte)(ssrc >> 8); rtp[11] = (byte)ssrc;
            Array.Copy(payload, offset, rtp, 12, length);

            var frame = new byte[4 + rtp.Length];
            frame[0] = 0x24; // '$'
            frame[1] = channel;
            frame[2] = (byte)(rtp.Length >> 8);
            frame[3] = (byte)rtp.Length;
            Array.Copy(rtp, 0, frame, 4, rtp.Length);

            try { lock (ns) { ns.Write(frame, 0, frame.Length); ns.Flush(); } }
            catch { alive = false; }
        }
    }
}