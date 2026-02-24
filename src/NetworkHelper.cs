
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class NetworkHelper
    {
        public static string GetLocalIPAddress()
        {
            try
            {
                var ip = GetIPViaUdpConnection();
                if (ip != null) return ip;

                ip = GetIPFromNetworkInterfaces();
                if (ip != null) return ip;

                ip = GetIPFromDns();
                if (ip != null) return ip;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error getting IP: {ex.Message}");
            }

            return "0.0.0.0";
        }

        private static string GetIPViaUdpConnection()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                if (endPoint != null && !IPAddress.IsLoopback(endPoint.Address))
                {
                    return endPoint.Address.ToString();
                }
            }
            catch { }
            return null;
        }

        private static string GetIPFromNetworkInterfaces()
        {
            try
            {
                foreach (var netInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (netInterface.OperationalStatus == OperationalStatus.Up &&
                        netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        netInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    {
                        if (IsVirtualInterface(netInterface.Description))
                            continue;

                        var ipProps = netInterface.GetIPProperties();

                        if (ipProps.GatewayAddresses.Count == 0)
                            continue;

                        foreach (var addr in ipProps.UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(addr.Address))
                            {
                                return addr.Address.ToString();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string GetIPFromDns()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                return host.AddressList
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                                         !IPAddress.IsLoopback(ip))?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsVirtualInterface(string description)
        {
            var virtualKeywords = new[] {
            "virtual", "vmware", "vbox", "docker", "hyper-v",
            "vEthernet", "bridge", "vnet", "tap", "tun"
        };

            return virtualKeywords.Any(keyword =>
                description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

    }
}