using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ContinuityHub.Platform;

internal static class LocalNet
{
    private static readonly string[] VirtualHints =
        { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "tap", "tunnel", "vpn", "loopback", "bluetooth" };

    /// <summary>LAN IPv4 addresses, best first: Wi-Fi/Ethernet adapters that have a default gateway.</summary>
    public static IReadOnlyList<IPAddress> CandidateIPv4()
    {
        var list = new List<(IPAddress ip, int score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            bool isVirtual = VirtualHints.Any(desc.Contains);

            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                              && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local
                int score = (hasGateway ? 100 : 0) + (isVirtual ? -50 : 0)
                          + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 20 : 0)
                          + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 10 : 0);
                list.Add((ua.Address, score));
            }
        }
        return list.OrderByDescending(x => x.score).Select(x => x.ip).ToList();
    }
}
