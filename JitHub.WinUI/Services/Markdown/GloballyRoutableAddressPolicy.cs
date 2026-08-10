using System;
using System.Net;
using System.Net.Sockets;

namespace JitHub.Services.Markdown;

/// <summary>
/// Allows only globally routable unicast destinations for remote Markdown assets.
/// This deliberately rejects every private, local, documentation, benchmarking,
/// transition, multicast, reserved, and other special-use range.
/// </summary>
internal static class GloballyRoutableAddressPolicy
{
    public static bool IsGloballyRoutable(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsGloballyRoutable(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsGloballyRoutableIPv4(bytes);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || bytes.Length != 16)
        {
            return false;
        }

        // Public IPv6 unicast is allocated from 2000::/3. Reject the special-use
        // sub-ranges inside it as well as deprecated 6to4 space.
        if ((bytes[0] & 0xE0) != 0x20)
        {
            return false;
        }

        bool ietfSpecialPurpose = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01;
        bool benchmarking = HasPrefix(bytes, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00], 48);
        bool automaticMulticastTunneling = HasPrefix(bytes, [0x20, 0x01, 0x00, 0x03], 32);
        bool as112V6 = HasPrefix(bytes, [0x20, 0x01, 0x00, 0x04, 0x01, 0x12], 48);
        bool orchid = HasPrefix(bytes, [0x20, 0x01, 0x00, 0x10], 28) ||
            HasPrefix(bytes, [0x20, 0x01, 0x00, 0x20], 28) ||
            HasPrefix(bytes, [0x20, 0x01, 0x00, 0x30], 28);
        bool documentation = HasPrefix(bytes, [0x20, 0x01, 0x0D, 0xB8], 32);
        bool deprecatedSixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        bool directDelegationAs112 = HasPrefix(bytes, [0x26, 0x20, 0x00, 0x4F, 0x80, 0x00], 48);
        bool documentationV2 = HasPrefix(bytes, [0x3F, 0xFF, 0x00], 20);
        return !ietfSpecialPurpose &&
            !benchmarking &&
            !automaticMulticastTunneling &&
            !as112V6 &&
            !orchid &&
            !documentation &&
            !deprecatedSixToFour &&
            !directDelegationAs112 &&
            !documentationV2;
    }

    public static bool IsSpecialUseHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        host = host.Trim().TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            HasSuffix(host, ".localhost") ||
            HasSuffix(host, ".local") ||
            HasSuffix(host, ".internal") ||
            host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase) ||
            HasSuffix(host, ".home.arpa") ||
            host.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            HasSuffix(host, ".test") ||
            host.Equals("invalid", StringComparison.OrdinalIgnoreCase) ||
            HasSuffix(host, ".invalid") ||
            host.Equals("example", StringComparison.OrdinalIgnoreCase) ||
            HasSuffix(host, ".example");
    }

    private static bool IsGloballyRoutableIPv4(byte[] bytes)
    {
        if (bytes.Length != 4)
        {
            return false;
        }

        byte a = bytes[0];
        byte b = bytes[1];
        byte c = bytes[2];
        return a != 0 &&
            a != 10 &&
            !(a == 100 && b is >= 64 and <= 127) &&
            a != 127 &&
            !(a == 169 && b == 254) &&
            !(a == 172 && b is >= 16 and <= 31) &&
            !(a == 192 && b == 0 && c == 0) &&
            !(a == 192 && b == 0 && c == 2) &&
            !(a == 192 && b == 31 && c == 196) &&
            !(a == 192 && b == 52 && c == 193) &&
            !(a == 192 && b == 88 && c == 99) &&
            !(a == 192 && b == 168) &&
            !(a == 192 && b == 175 && c == 48) &&
            !(a == 198 && b is 18 or 19) &&
            !(a == 198 && b == 51 && c == 100) &&
            !(a == 203 && b == 0 && c == 113) &&
            a < 224;
    }

    private static bool HasSuffix(string host, string suffix) =>
        host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool HasPrefix(byte[] address, ReadOnlySpan<byte> prefix, int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        int requiredBytes = wholeBytes + (remainingBits == 0 ? 0 : 1);
        if (address.Length < requiredBytes || prefix.Length < requiredBytes)
        {
            return false;
        }

        if (!address.AsSpan(0, wholeBytes).SequenceEqual(prefix[..wholeBytes]))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xFF << (8 - remainingBits);
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }
}
