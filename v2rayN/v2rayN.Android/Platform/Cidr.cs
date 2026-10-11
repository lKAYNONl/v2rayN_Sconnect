using System.Net;
using System.Net.Sockets;

namespace v2rayN.Mobile.Platform;

/// <summary>
/// An IPv4 or IPv6 prefix with host bits cleared - the form
/// VpnService.Builder.addRoute insists on. Subtraction lets us express
/// "everything except X" with plain routes on every Android version
/// (excludeRoute only exists from API 33).
/// </summary>
internal readonly record struct Cidr(UInt128 Network, int Length, bool IsV6)
{
    public int Bits => IsV6 ? 128 : 32;

    public static readonly Cidr AnyV4 = new(UInt128.Zero, 0, false);
    public static readonly Cidr AnyV6 = new(UInt128.Zero, 0, true);

    public static bool TryParse(string? text, out Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        var slash = s.IndexOf('/');
        var addressText = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        var bits = v6 ? 128 : 32;
        var length = bits;
        if (slash >= 0
            && (!int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out length)
                || length < 0
                || length > bits))
        {
            return false;
        }

        var value = UInt128.Zero;
        foreach (var b in address.GetAddressBytes())
        {
            value = (value << 8) | b;
        }

        cidr = new Cidr(Mask(value, length, bits), length, v6);
        return true;
    }

    public static Cidr Parse(string text)
        => TryParse(text, out var cidr) ? cidr : throw new FormatException($"Not a subnet: {text}");

    /// <summary>All bits set in the lowest <paramref name="count"/> positions.</summary>
    private static UInt128 LowBits(int count)
        => count <= 0 ? UInt128.Zero
            : count >= 128 ? UInt128.MaxValue
            : (UInt128.One << count) - UInt128.One;

    private static UInt128 Mask(UInt128 value, int length, int bits)
        => value & (LowBits(bits) & ~LowBits(bits - length));

    /// <summary>True when <paramref name="other"/> lies inside this prefix (or equals it).</summary>
    public bool Contains(Cidr other)
        => IsV6 == other.IsV6 && Length <= other.Length && Mask(other.Network, Length, Bits) == Network;

    public IPAddress ToAddress()
    {
        var bytes = new byte[Bits / 8];
        var value = Network;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFFu);
            value >>= 8;
        }
        return new IPAddress(bytes);
    }

    public string AddressText => ToAddress().ToString();

    public override string ToString() => $"{AddressText}/{Length}";

    /// <summary>
    /// Every address of <paramref name="include"/> that is not in
    /// <paramref name="exclude"/>, as a list of prefixes.
    /// </summary>
    public static List<Cidr> Subtract(IEnumerable<Cidr> include, IEnumerable<Cidr> exclude)
    {
        var result = RemoveCovered(include);
        foreach (var hole in exclude)
        {
            var next = new List<Cidr>(result.Count + 8);
            foreach (var prefix in result)
            {
                SubtractOne(prefix, hole, next);
            }
            result = next;
        }
        return result;
    }

    private static void SubtractOne(Cidr prefix, Cidr hole, List<Cidr> output)
    {
        if (prefix.IsV6 != hole.IsV6 || !(prefix.Contains(hole) || hole.Contains(prefix)))
        {
            output.Add(prefix);         // disjoint
            return;
        }
        if (hole.Contains(prefix))
        {
            return;                     // removed entirely
        }

        // The hole is strictly inside: split in halves and keep the parts around it.
        var length = prefix.Length + 1;
        var upperBit = UInt128.One << (prefix.Bits - length);
        SubtractOne(prefix with { Length = length }, hole, output);
        SubtractOne(new Cidr(prefix.Network | upperBit, length, prefix.IsV6), hole, output);
    }

    /// <summary>Drops duplicates and prefixes already covered by a wider one.</summary>
    private static List<Cidr> RemoveCovered(IEnumerable<Cidr> prefixes)
    {
        var sorted = prefixes.Distinct().OrderBy(p => p.Length).ToList();
        var result = new List<Cidr>(sorted.Count);
        foreach (var p in sorted)
        {
            if (!result.Any(r => r.Contains(p)))
            {
                result.Add(p);
            }
        }
        return result;
    }
}
