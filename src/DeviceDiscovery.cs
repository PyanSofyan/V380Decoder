using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace V380Decoder.src
{
    public class DeviceInfo
    {
        public string Mac { get; set; }
        public string DevId { get; set; }
        public string Ip { get; set; }
        public string Subnet { get; set; }
        public string Gateway { get; set; }

    }
    public class DeviceDiscovery
    {
        private List<DeviceInfo> _devices = [];

        public List<DeviceInfo> Discover()
        {
            UdpClient udpClient = new();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 10009));
            udpClient.EnableBroadcast = true;
            udpClient.Client.ReceiveTimeout = 250;
            try
            {
                IPEndPoint broadcastEndpoint = new(IPAddress.Broadcast, 10008);
                byte[] searchCmd = Encoding.ASCII.GetBytes("NVDEVSEARCH^100");
                int retry = 5;

                while (retry-- > 0)
                {
                    udpClient.Send(searchCmd, searchCmd.Length, broadcastEndpoint);

                    var startTick = Environment.TickCount;
                    while (Environment.TickCount - startTick < 250)
                    {
                        try
                        {
                            IPEndPoint remoteEP = null;
                            byte[] receivedData = udpClient.Receive(ref remoteEP);
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
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DISCOVERY] Error: {ex.Message}");
            }
            finally
            {
                udpClient.Close();
                udpClient.Dispose();
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

            _devices.Add(new DeviceInfo
            {
                Mac = mac,
                DevId = parts[12],
                Ip = parts[3],
                Subnet = parts[4],
                Gateway = parts[5]
            });
        }
    }
}