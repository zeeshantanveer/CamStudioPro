using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CamStudio.Desktop.Core;

public static class NetInfo
{
    /// <summary>IPv4 addresses of up, non-loopback, non-virtual-looking adapters, best first.</summary>
    public static List<IPAddress> LocalIPv4()
    {
        var result = new List<(IPAddress ip, int rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;

                var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
                var virtualish = desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("vmware") ||
                                 desc.Contains("vbox") || desc.Contains("wsl") || desc.Contains("vethernet") || desc.Contains("default switch") || desc.Contains("vpn") || desc.Contains("tap");
                var rank = virtualish ? 3 : props.GatewayAddresses.Count > 0 ? (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1) : 2;
                result.Add((ua.Address, rank));
            }
        }
        return result.OrderBy(r => r.rank).Select(r => r.ip).Distinct().ToList();
    }

    public static string OsName()
    {
        var build = Environment.OSVersion.Version.Build;
        return build >= 22000 ? "Windows 11" : "Windows 10";
    }
}
