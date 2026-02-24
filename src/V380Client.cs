using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace V380Decoder.src
{
    public class V380Client : IDisposable
    {
        public SnapshotManager snapshotManager { get; private set; }
        private TcpClient authClient, streamClient;
        private NetworkStream authStream, streamStream;
        private readonly string ip;
        private readonly int port;
        private readonly uint deviceId;
        private readonly string username, password;
        private readonly SourceStream source;
        private readonly OutputMode mode;
        private uint authTicket, sessionId;
        private ushort deviceVersion, communicationVersion;
        private int frameWidth = 1280;
        private int frameheight = 720;
        private byte[] aesKey = new byte[16];
        private bool needReconnect = false;

        public V380Client(string ip, int port, uint deviceId, string username, string password, SourceStream source, OutputMode mode)
        {
            this.ip = ip;
            this.port = port;
            this.deviceId = deviceId;
            this.username = username;
            this.password = password;
            this.source = source;
            this.mode = mode;
            if (mode == OutputMode.Rtsp)
                snapshotManager = new SnapshotManager();
        }

        public void Run(RtspServer rtsp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int auth = GetAuthTicket();
                    if (auth == 0) continue;
                    if (auth == -1) break;

                    if (!StreamLogin())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        streamStream?.Close(); streamClient?.Close();
                        continue;
                    }

                    if (!StartStream())
                    {
                        Console.Error.WriteLine("[STREAM] Retrying...");
                        continue;
                    }

                    ReceiveFrames(mode, rtsp, ct);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("[STREAM] Operation cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ERROR] Fatal error: {ex.Message}");
                    Console.Error.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
                    Thread.Sleep(3000);
                }
                finally
                {
                    Console.Error.WriteLine("[STREAM] Closing connection...");
                    streamStream?.Close(); streamClient?.Close();
                }
            }
        }

        public int GetAuthTicket()
        {
            try
            {
                authClient = new TcpClient();
                var r = authClient.BeginConnect(ip, port, null, null);
                if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("[AUTH] failed connecting to socket. retrying...");
                    return 0;
                }
                authClient.EndConnect(r);
                authStream = authClient.GetStream();

                var encryptedPassword = GeneratePassword(password);
                var userBytes = Encoding.ASCII.GetBytes(username);
                var cmd1167 = new byte[520];
                WriteUInt32LE(cmd1167, 0, 1167); // command
                if (source == SourceStream.Lan)
                {
                    WriteUInt32LE(cmd1167, 4, 120); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3            
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    Array.Copy(userBytes, 0, cmd1167, 49, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 81, Math.Min(encryptedPassword.Length, 64)); //password
                }
                else
                {
                    WriteUInt32LE(cmd1167, 4, 1022); // unknown1
                    cmd1167[8] = 31; // unknown2 (version?)
                    WriteUInt32LE(cmd1167, 9, 1); // unknown3 
                    WriteUInt32LE(cmd1167, 13, deviceId); // deviceId
                    var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                    Array.Copy(hostnameBytes, 0, cmd1167, 17, Math.Min(hostnameBytes.Length, 50)); //hostname
                    WriteUInt32LE(cmd1167, 67, (uint)port); //port
                    Array.Copy(userBytes, 0, cmd1167, 71, Math.Min(userBytes.Length, 32)); //username
                    Array.Copy(encryptedPassword, 0, cmd1167, 103, Math.Min(encryptedPassword.Length, 64)); //password
                }
                if (!SendData(authStream, cmd1167))
                {
                    Console.Error.WriteLine("[AUTH] failed send request. retrying...");
                    return 0;
                }

                var resp = ReceiveData(authStream, 256);
                if (resp == null || resp.Length < 256)
                {
                    Console.Error.WriteLine("[AUTH] failed receive response. retrying...");
                    return 0;
                }

                uint respCmd = ReadUInt32LE(resp, 0);
                if (respCmd != 1168)
                {
                    Console.Error.WriteLine($"[AUTH] invalid response cmd: {respCmd} (expected 1168). retrying...");
                    return 0;
                }
                uint loginResult = ReadUInt32LE(resp, 4);
                if (loginResult != 1001)
                {
                    if (loginResult == 1011)
                        Console.Error.WriteLine($"[AUTH] invalid username. exiting...");
                    else if (loginResult == 1012)
                        Console.Error.WriteLine($"[AUTH] invalid password. exiting...");
                    else if (loginResult == 1018)
                        Console.Error.WriteLine($"[AUTH] invalid device id. exiting...");
                    else
                        Console.Error.WriteLine($"[AUTH] login failed result: {loginResult} (expected 1001). exiting...");

                    return -1;
                }

                uint resultValue = ReadUInt32LE(resp, 8);
                byte version = resp[12];
                uint ticket = ReadUInt32LE(resp, 13);
                uint session = ReadUInt32LE(resp, 17);
                byte deviceType = resp[21];
                byte camType = resp[22];
                uint vendorId = ReadUInt16LE(resp, 23);
                uint isDomainExists = resp[25];
                byte[] domainBytes = new byte[32];
                Array.Copy(resp, 26, domainBytes, 0, 32);
                string domain = Encoding.ASCII.GetString(domainBytes).TrimEnd('\0');

                LogUtils.debug($"[AUTH] response success");
                LogUtils.debug($"[AUTH] cmd: {respCmd}");
                LogUtils.debug($"[AUTH] result: {loginResult}");
                LogUtils.debug($"[AUTH] authTicket: {ticket} (0x{ticket:X})");
                LogUtils.debug($"[AUTH] resultValue: {resultValue}");
                LogUtils.debug($"[AUTH] version: {version}");
                LogUtils.debug($"[AUTH] session: {session}");
                LogUtils.debug($"[AUTH] deviceType: {deviceType}");
                LogUtils.debug($"[AUTH] camType: {camType}");
                LogUtils.debug($"[AUTH] vendorId: {vendorId}");
                LogUtils.debug($"[AUTH] isDomainExists: {isDomainExists}");
                LogUtils.debug($"[AUTH] domain: {domain}");

                deviceVersion = version;
                sessionId = session;
                authTicket = ticket;

                Console.Error.WriteLine($"[AUTH] success ticket={authTicket} deviceVersion={deviceVersion}");
                authStream.Close(); authClient.Close();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AUTH] Error: {ex.Message}");
                return 0;
            }
            finally
            {
                authStream?.Close(); authClient?.Close();
            }
        }


        public bool StreamLogin()
        {
            streamClient = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 0x8000
            };
            var r = streamClient.BeginConnect(ip, port, null, null);
            if (!r.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("[STREAM] failed connecting to socket. retrying...");
                return false;
            }
            ;
            streamClient.EndConnect(r);
            streamStream = streamClient.GetStream();

            var cmd301 = new byte[256];
            WriteUInt32LE(cmd301, 0, 301);
            if (source == SourceStream.Lan)
            {
                WriteUInt32LE(cmd301, 4, deviceId); //device id
                WriteUInt32LE(cmd301, 8, 0); //unknown1 
                WriteUInt16LE(cmd301, 12, 20); //unknown2 
                WriteUInt32LE(cmd301, 14, authTicket); //auth ticket
                WriteUInt32LE(cmd301, 22, 4097); //audio 4096=off, 4097=on
                WriteUInt32LE(cmd301, 26, 1);    //quality  0=SD, 1=HD
            }
            else
            {
                WriteUInt32LE(cmd301, 4, 1022); //unknown1 
                var hostnameBytes = Encoding.ASCII.GetBytes($"{deviceId}.nvdvr.net");
                Array.Copy(hostnameBytes, 0, cmd301, 8, Math.Min(hostnameBytes.Length, 50)); //hostname
                WriteUInt32LE(cmd301, 58, (uint)port); //port
                WriteUInt32LE(cmd301, 62, deviceId); //device id
                WriteUInt32LE(cmd301, 66, authTicket); // auth ticket
                WriteUInt32LE(cmd301, 70, sessionId); // session id
                WriteUInt32LE(cmd301, 74, 1); //quality 0=SD, 1=HD
                cmd301[78] = 20; //unknown2 
                WriteUInt32LE(cmd301, 79, 1); //unknown23
            }

            if (!SendData(streamStream, cmd301))
            {
                Console.Error.WriteLine("[STREAM] login failed send request");
                return false;
            }

            var resp401 = ReceiveData(streamStream, 412);

            if (resp401 == null || resp401.Length < 8)
            {
                Console.Error.WriteLine("[STREAM] login failed receive response");
                return false;
            }
            var respCmd = ReadUInt32LE(resp401, 0);
            if (respCmd != 401)
            {
                Console.Error.WriteLine($"[STREAM] login invalid response cmd: {respCmd} (expected 401)");
                return false;
            }

            int result = (int)ReadUInt32LE(resp401, 4);
            if (result == -11 || result == -12)
            {
                Console.Error.WriteLine($"[STREAM] login failed result={result}");
                return false;
            }

            LogUtils.debug($"[STREAM] login response success");
            LogUtils.debug($"[STREAM] login cmd: {respCmd}");
            if (source == SourceStream.Lan)
            {
                ushort version = ReadUInt16LE(resp401, 8);
                uint width = ReadUInt32LE(resp401, 10);
                uint height = ReadUInt32LE(resp401, 14);
                uint maxPackSize = ReadUInt32LE(resp401, 18);
                byte audioFreq = resp401[22];
                byte audioBits = resp401[23];
                byte audioChannels = resp401[24];
                communicationVersion = version;
                frameWidth = (int)width;
                frameheight = (int)height;
                LogUtils.debug($"[STREAM] login result: {result}");
                LogUtils.debug($"[STREAM] login version: {version}");
                LogUtils.debug($"[STREAM] login width: {width}");
                LogUtils.debug($"[STREAM] login height: {height}");
                LogUtils.debug($"[STREAM] login maxPackSize: {maxPackSize}");
                LogUtils.debug($"[STREAM] login audioFreq: {audioFreq}");
                LogUtils.debug($"[STREAM] login audioBits: {audioBits}");
                LogUtils.debug($"[STREAM] login audioChannels: {audioChannels}");
            }

            if (deviceVersion > 30) GenerateMediaKey(authTicket);
            Console.Error.WriteLine($"[STREAM] login OK");
            return true;
        }

        public bool StartStream()
        {
            Console.Error.WriteLine($"[STREAM] starting stream...");
            var cmd303 = new byte[256];
            WriteUInt32LE(cmd303, 0, 303);
            WriteUInt16LE(cmd303, 4, 0x3001);
            return SendData(streamStream, cmd303);
        }

        public void ReceiveFrames(OutputMode mode, RtspServer rtsp, CancellationToken ct)
        {
            bool needDecrypt = deviceVersion > 30;

            var videoFrags = new List<byte>();
            var audioFrags = new List<byte>();
            ushort videoTotal = 0, audioTotal = 0;

            var header12 = new byte[12];
            var payloadBuf = new byte[65536];

            // stdout is used only for Video or Audio output modes
            Stream stdout = (mode == OutputMode.Video || mode == OutputMode.Audio)
                ? Console.OpenStandardOutput()
                : null;

            Console.Error.WriteLine($"[RECV] mode={mode} decrypt={needDecrypt} communicationVersion={communicationVersion}");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (needReconnect)
                    {
                        Console.Error.WriteLine($"[STREAM] lost, reconnecting... ");
                        try { streamStream?.Close(); streamClient?.Close(); } catch { }
                        if (!StreamLogin()) break;
                        if (!StartStream()) break;
                        needReconnect = false;
                    }

                    // 12-byte fragment header
                    if (ReadExact(streamStream, header12, 0, 12) < 12) continue;

                    if (header12[0] != 0x7F)
                    {
                        continue;
                    }

                    byte type = header12[1];
                    ushort totalFrame = ReadUInt16LE(header12, 3);
                    ushort curFrame = ReadUInt16LE(header12, 5);
                    ushort payLen = ReadUInt16LE(header12, 7);

                    if (payLen == 0 || payLen > 20000 || totalFrame == 0 || curFrame >= totalFrame)
                    {
                        Console.Error.WriteLine($"[SKIP] invalid header type=0x{type:X2} total={totalFrame} cur={curFrame} len={payLen}");
                        continue;
                    }

                    if (payloadBuf.Length < payLen) payloadBuf = new byte[payLen];
                    if (ReadExact(streamStream, payloadBuf, 0, payLen) < payLen) continue;

                    // VIDEO  0x00=I-frame  0x01=P-frame
                    if (type == 0x00 || type == 0x01)
                    {
                        if (curFrame == 0) { videoFrags.Clear(); videoTotal = totalFrame; }
                        if (totalFrame != videoTotal) { videoFrags.Clear(); videoTotal = totalFrame; }

                        for (int i = 0; i < payLen; i++) videoFrags.Add(payloadBuf[i]);

                        if (curFrame != totalFrame - 1) continue;
                        if (videoFrags.Count < 16) { videoFrags.Clear(); continue; }

                        byte[] full = videoFrags.ToArray();
                        videoFrags.Clear();

                        //parse inner 16-byte frame header
                        uint frameId = ReadUInt32LE(full, 0);
                        ushort frameType = ReadUInt16LE(full, 4);
                        ushort frameRate = ReadUInt16LE(full, 6);
                        ulong timestamp = ReadUInt64LE(full, 8);

                        byte[] payload = new byte[full.Length - 16];
                        Array.Copy(full, 16, payload, 0, payload.Length);

                        if (needDecrypt)
                        {
                            if (communicationVersion == 21)
                                DecryptMediaPre2k(payload, payload.Length, 1);
                            else
                                DecryptVideoFrame(payload, payload.Length);
                        }

                        // validate H.264 start code
                        if (payload.Length < 4 ||
                            payload[0] != 0 || payload[1] != 0 ||
                            payload[2] != 0 || payload[3] != 1)
                        {
                            Console.Error.WriteLine($"[VIDEO] bad start code, len={payload.Length}");
                            continue;
                        }

                        if (type == 0x00 && mode == OutputMode.Rtsp)
                        {
                            snapshotManager.UpdateFrame(payload, frameWidth, frameheight);
                        }

                        var fd = new FrameData
                        {
                            RawType = type,
                            FrameId = frameId,
                            FrameType = frameType,
                            FrameRate = frameRate,
                            Timestamp = timestamp,
                            Payload = payload
                        };

                        if (mode == OutputMode.Video)
                        {
                            stdout.Write(payload, 0, payload.Length);
                            stdout.Flush();
                        }
                        else if (mode == OutputMode.Rtsp)
                        {
                            rtsp?.PushVideo(fd);
                        }
                    }

                    // AUDIO  0x1A
                    else if (type == 0x1A)
                    {
                        if (curFrame == 0) { audioFrags.Clear(); audioTotal = totalFrame; }
                        if (totalFrame != audioTotal) { audioFrags.Clear(); audioTotal = totalFrame; }

                        for (int i = 0; i < payLen; i++) audioFrags.Add(payloadBuf[i]);

                        if (curFrame != totalFrame - 1) continue;
                        if (audioFrags.Count < 16) { audioFrags.Clear(); continue; }

                        byte[] full = audioFrags.ToArray();
                        audioFrags.Clear();

                        //parse inner 16-byte frame header
                        uint frameId = ReadUInt32LE(full, 0);
                        ushort frameType = ReadUInt16LE(full, 4);
                        ushort frameRate = ReadUInt16LE(full, 6);
                        ulong timestamp = ReadUInt64LE(full, 8);

                        byte[] payload = new byte[full.Length - 16];
                        Array.Copy(full, 16, payload, 0, payload.Length);

                        if (needDecrypt)
                        {
                            if (communicationVersion == 21)
                                DecryptMediaPre2k(payload, payload.Length, 1);
                            else
                                DecryptAudioFrame(payload, payload.Length);
                        }

                        var fd = new FrameData
                        {
                            RawType = type,
                            FrameId = frameId,
                            FrameType = frameType,
                            FrameRate = frameRate,
                            Timestamp = timestamp,
                            Payload = payload
                        };

                        if (mode == OutputMode.Audio)
                        {
                            stdout.Write(payload, 0, payload.Length);
                            stdout.Flush();
                        }
                        else if (mode == OutputMode.Rtsp)
                        {
                            rtsp?.PushAudio(fd);
                        }
                    }
                    else if (type == 0x5B)
                    {
                        continue;
                    }
                    else
                    {
                        Console.Error.WriteLine($"[FRAME] unknown type=0x{type:X2} len={payLen}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[RECV] Operation cancelled");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RECV] {ex.Message}");
            }
        }

        private void DecryptVideoFrame(byte[] data, int length)
        {
            using var aes = Aes.Create();
            aes.Key = aesKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int offset = 0; offset + 64 <= length; offset += 80)
                for (int i = 0; i < 4; i++)
                    dec.TransformBlock(data, offset + i * 16, 16, data, offset + i * 16);
        }

        private void DecryptAudioFrame(byte[] data, int length)
        {
            int alignedSize = (length / 16) * 16;
            if (alignedSize == 0) return;

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            for (int i = 0; i < alignedSize; i += 16)
                dec.TransformBlock(data, i, 16, data, i);
        }

        // Not tested
        private void DecryptMediaPre2k(byte[] data, int length, int mode)
        {
            int decryptLength;
            if (mode == 0 && length > 2048)
            {
                decryptLength = 2048;
            }
            else
            {
                decryptLength = (length / 16) * 16;
            }
            if (decryptLength > 0)
            {
                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var decryptor = aes.CreateDecryptor();
                decryptor.TransformBlock(data, 0, decryptLength, data, 0);
            }
        }

        void GenerateMediaKey(uint ticket)
        {
            WriteUInt32LE(aesKey, 0, ticket);
            WriteUInt64LE(aesKey, 4, 0x618123462c14795c);
            WriteUInt32LE(aesKey, 12, 0x82800df0);
            LogUtils.debug($"[KEY] {BitConverter.ToString(aesKey).Replace("-", "")}");
        }

        byte[] GeneratePassword(string pw)
        {
            byte[] sk = Encoding.ASCII.GetBytes("macrovideo+*#!^@");
            var rng = new Random();
            byte[] rk = new byte[16];
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            for (int i = 0; i < 16; i++) rk[i] = (byte)chars[rng.Next(chars.Length)];

            var pb = Encoding.ASCII.GetBytes(pw);
            var pad = new byte[48];
            Array.Copy(pb, pad, Math.Min(pb.Length, 48));

            void Enc(byte[] key)
            {
                using var a = Aes.Create();
                a.Key = key; a.Mode = CipherMode.ECB; a.Padding = PaddingMode.None;
                using var e = a.CreateEncryptor();
                for (int i = 0; i < 48; i += 16) e.TransformBlock(pad, i, 16, pad, i);
            }
            Enc(sk); Enc(rk);

            var out_ = new byte[64];
            Array.Copy(rk, 0, out_, 0, 16);
            Array.Copy(pad, 0, out_, 16, 48);
            return out_;
        }

        bool SendData(NetworkStream s, byte[] d)
        {
            try { s.Write(d, 0, d.Length); s.Flush(); return true; }
            catch (Exception ex) { Console.Error.WriteLine($"[SEND] {ex.Message}"); return false; }
        }

        byte[] ReceiveData(NetworkStream s, int max)
        {
            var buf = new byte[max]; int tot = 0;
            var deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                if (s.DataAvailable || tot > 0)
                {
                    int n = s.Read(buf, tot, max - tot);
                    if (n <= 0) break;
                    tot += n;
                    if (tot >= 16) break;
                }
                else Thread.Sleep(10);
            }
            if (tot == 0) return null;
            var r = new byte[tot]; Array.Copy(buf, r, tot); return r;
        }

        int ReadExact(NetworkStream s, byte[] buf, int off, int cnt)
        {
            int tot = 0;
            var deadline = DateTime.Now.AddSeconds(3);
            while (tot < cnt)
            {
                if (DateTime.Now > deadline)
                {
                    needReconnect = true;
                    return tot;
                }
                if (!s.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int n = s.Read(buf, off + tot, cnt - tot);
                if (n <= 0) break;
                tot += n;
            }
            return tot;
        }

        void WriteUInt32LE(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        void WriteUInt16LE(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        void WriteUInt64LE(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (i * 8)); }
        uint ReadUInt32LE(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        ushort ReadUInt16LE(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        ulong ReadUInt64LE(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v |= ((ulong)b[o + i]) << (i * 8); return v; }

        public bool PtzRight() => SendControl(V380Commands.PTZ_RIGHT);
        public bool PtzLeft() => SendControl(V380Commands.PTZ_LEFT);
        public bool PtzUp() => SendControl(V380Commands.PTZ_UP);
        public bool PtzDown() => SendControl(V380Commands.PTZ_DOWN);
        public bool PtzStop() => SendControl(V380Commands.PTZ_STOP);
        public bool LightOn() => SendControl(V380Commands.LIGHT_ON);
        public bool LightOff() => SendControl(V380Commands.LIGHT_OFF);
        public bool LightAuto() => SendControl(V380Commands.LIGHT_AUTO);
        public bool ImageColor() => SendControl(V380Commands.IMAGE_COLOR);
        public bool ImageBW() => SendControl(V380Commands.IMAGE_BW);
        public bool ImageAuto() => SendControl(V380Commands.IMAGE_AUTO);
        public bool ImageFlip() => SendControl(V380Commands.IMAGE_FLIP);
        private bool SendControl(byte[] payload)
        {
            if (streamStream == null) return false;
            return SendData(streamStream, payload);
        }

        public string GetDeviceId()
        {
            return deviceId.ToString();
        }

        public string GetDeviceVersion()
        {
            return deviceVersion.ToString();
        }

        public void Dispose()
        {
            authStream?.Close(); authClient?.Close();
            streamStream?.Close(); streamClient?.Close();
            snapshotManager?.Dispose();
        }
    }
}