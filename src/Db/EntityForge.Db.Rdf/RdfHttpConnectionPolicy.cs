using System.Net;
using System.Net.Sockets;

namespace EntityForge.Db.Rdf;

internal static class RdfHttpConnectionPolicy
{
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");
    private static readonly IPAddress AzurePlatform = IPAddress.Parse("168.63.129.16");

    internal static bool IsAllowedAddress(IPAddress address, bool allowPrivateNetwork)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.Equals(AwsMetadataV6) || address.Equals(AzurePlatform))
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bool privateAddress = bytes[0] == 127 || bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
            if (privateAddress)
            {
                return allowPrivateNetwork;
            }
            // Unspecified, link-local/metadata, shared-address, multicast,
            // documentation, benchmark and reserved ranges are never destinations.
            return !(bytes[0] == 0 || bytes[0] >= 224 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
                (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0)
        {
            return false;
        }
        if (IPAddress.IsLoopback(address) || (bytes[0] & 0xfe) == 0xfc)
        {
            return allowPrivateNetwork;
        }
        // Only global unicast; deny special-purpose, documentation and 6to4
        // prefixes rather than allowing an embedded private IPv4 destination.
        return (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 1) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowPrivateNetwork,
        CancellationToken cancellation,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        cancellation.ThrowIfCancellationRequested();
        IPAddress[] addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal] : await (resolve ?? Dns.GetHostAddressesAsync)(endpoint.Host, cancellation).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsAllowedAddress(address, allowPrivateNetwork)))
        {
            throw new HttpRequestException("The SPARQL HTTP destination resolves to an address blocked by the network policy.");
        }
        // Resolve once, validate the entire answer, then connect to the actual IP.
        // No second hostname lookup may occur between validation and connection.
        SocketException? failure = null;
        foreach (IPAddress address in addresses.Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                return await (connect ?? ConnectSocketAsync)(address, endpoint.Port, cancellation).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                failure = exception;
            }
        }
        throw new HttpRequestException("Cannot connect to the configured SPARQL HTTP destination.", failure);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken cancellation)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellation).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
