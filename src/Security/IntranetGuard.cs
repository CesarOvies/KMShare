using System.Net;
using System.Net.Sockets;

namespace KMShare.Security;

public sealed class IntranetSecurityException(string message) : Exception(message);

public static class IntranetGuard
{
    public static bool IsStrictlyLanIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            ip.TryWriteBytes(b, out _);
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            Span<byte> b = stackalloc byte[16];
            ip.TryWriteBytes(b, out _);
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 Unique Local Address
        }

        return false;
    }

    public static void EnforceLanEndpoint(IPEndPoint endpoint)
    {
        if (!IsStrictlyLanIp(endpoint.Address))
        {
            throw new IntranetSecurityException(
                $"OUTBOUND_INTERNET_BLOCKED: Endpoint {endpoint} is outside local intranet.");
        }
    }
}

