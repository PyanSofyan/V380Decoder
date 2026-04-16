using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace V380Decoder.src
{
    public class OnvifDiscovery : IDisposable
    {
        private readonly int port;
        private UdpClient udp;
        private Thread acceptThread;
        private volatile bool running;

        public OnvifDiscovery(int port) { this.port = port; }

        public void Start()
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 3702));
            udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "onvif-discovery" };
            acceptThread.Start();
            LogUtils.debug("[ONVIF] Onvif Discovery listening...");
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    IPEndPoint remoteEP = null;
                    var result = udp.Receive(ref remoteEP);
                    var request = Encoding.UTF8.GetString(result);
                    if (request.Contains("Probe"))
                    {
                        LogUtils.debug("[ONVIF] Probe received");

                        var response = BuildProbeMatch(request);

                        var bytes = Encoding.UTF8.GetBytes(response);

                        udp.Send(bytes, bytes.Length, remoteEP);
                    }
                }
                catch { }
            }
        }

        static string ExtractMessageId(string xml)
        {
            var doc = XDocument.Parse(xml);
            XNamespace w = "http://schemas.xmlsoap.org/ws/2004/08/addressing";

            return doc.Descendants(w + "MessageID").FirstOrDefault()?.Value ?? "";
        }

        string BuildProbeMatch(string requestXml)
        {
            string messageId = ExtractMessageId(requestXml);
            string xaddr = $"http://{NetworkHelper.GetLocalIPAddress()}:{port}/onvif/device_service";

            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
                <e:Envelope xmlns:e=""http://www.w3.org/2003/05/soap-envelope""
                            xmlns:w=""http://schemas.xmlsoap.org/ws/2004/08/addressing""
                            xmlns:d=""http://schemas.xmlsoap.org/ws/2005/04/discovery""
                            xmlns:dn=""http://www.onvif.org/ver10/network/wsdl"">
                <e:Header>
                    <w:MessageID>uuid:{Guid.NewGuid()}</w:MessageID>
                    <w:RelatesTo>{messageId}</w:RelatesTo>
                    <w:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</w:To>
                    <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</w:Action>
                </e:Header>
                <e:Body>
                    <d:ProbeMatches>
                    <d:ProbeMatch>
                        <w:EndpointReference>
                        <w:Address>V380</w:Address>
                        </w:EndpointReference>
                        <d:Types>dn:NetworkVideoTransmitter</d:Types>
                        <d:Scopes>
                        onvif://www.onvif.org/type/video_encoder
                        onvif://www.onvif.org/Profile/Streaming
                        </d:Scopes>
                        <d:XAddrs>{xaddr}</d:XAddrs>
                        <d:MetadataVersion>1</d:MetadataVersion>
                    </d:ProbeMatch>
                    </d:ProbeMatches>
                </e:Body>
                </e:Envelope>";
        }

        public void Dispose()
        {
            running = false;

            // Tunggu thread selesai (maksimal 2 detik)
            try
            {
                if (acceptThread?.IsAlive == true)
                {
                    acceptThread.Join(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ONVIF] Error waiting for AcceptLoop: {ex.Message}");
            }

            try
            {
                udp?.Close();
                udp?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ONVIF] Error disposing UDP: {ex.Message}");
            }
        }
    }
}