using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public struct TDevice
    {
        public string Mac { get; set; }
        public string DevId { get; set; }
        public string Ip { get; set; }
    }
    public class DeviceDiscovery : IDisposable
    {
        private UdpClient _udpClient;
        private List<TDevice> _devices = [];

        public DeviceDiscovery()
        {
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 10009))
            {
                EnableBroadcast = true
            };
            _udpClient.Client.ReceiveTimeout = 250;
        }
        public List<TDevice> Discover()
        {
            IPEndPoint broadcastEndpoint = new(IPAddress.Broadcast, 10008);
            byte[] searchCmd = Encoding.ASCII.GetBytes("NVDEVSEARCH^100");
            int retry = 5;

            while (retry-- > 0)
            {
                _udpClient.Send(searchCmd, searchCmd.Length, broadcastEndpoint);

                var startTick = Environment.TickCount;
                while (Environment.TickCount - startTick < 250)
                {
                    try
                    {
                        IPEndPoint remoteEP = null;
                        byte[] receivedData = _udpClient.Receive(ref remoteEP);
                        string data = Encoding.ASCII.GetString(receivedData);
                        Parse(data, remoteEP.Address.ToString());
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode == SocketError.TimedOut)
                            break;
                        Console.Error.WriteLine($"[DISCOVERY] Error receive data: {ex.Message}");
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                }
            }

            return _devices;
        }

        private void Parse(string data, string sourceIp)
        {
            LogUtils.debug($"[DISCOVERY] result: {data}");
            string[] parts = data.Split('^');
            if (parts.Length < 13) return;
            if (parts[0] != "NVDEVRESULT") return;

            string mac = parts[2];
            foreach (var dev in _devices)
            {
                if (dev.Mac == mac)
                    return;
            }

            _devices.Add(new TDevice
            {
                Mac = mac,
                DevId = parts[12],
                Ip = parts[3]
            });
        }

        public void Dispose()
        {
            _udpClient?.Close();
            _udpClient = null;
        }
    }
}