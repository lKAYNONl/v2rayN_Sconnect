namespace v2rayN.Mobile.Platform;

/// <summary>
/// What goes into the VPN interface, computed from the same
/// <see cref="KillSwitchItem"/> the desktop uses:
/// <list type="bullet">
/// <item>apps - <see cref="KillSwitchItem.AndroidApps"/> become
///   addAllowedApplication (whitelist) or addDisallowedApplication (bypass);</item>
/// <item>subnets - <see cref="KillSwitchItem.BypassSubnets"/> become the only
///   routes (whitelist) or holes in 0.0.0.0/0 and ::/0 (bypass);</item>
/// <item>LAN - with <see cref="KillSwitchItem.AllowLan"/> private ranges stay
///   outside the tunnel.</item>
/// </list>
/// Apps and subnets combine with AND: a listed app's traffic to a listed
/// subnet. Android cannot express OR - routes apply to every app in the VPN.
/// </summary>
internal sealed class TunnelPlan
{
    /// <summary>The VPN interface address (and the /30 it lives in).</summary>
    public const string Address4 = "172.19.0.1";
    public const int Address4Prefix = 30;
    public const string Address6 = "fdfe:dcba:9876::1";
    public const int Address6Prefix = 126;

    /// <summary>
    /// DNS server announced to apps. Its queries enter the tunnel like any
    /// other packet: sing-box answers them itself (hijack-dns), Xray sends
    /// them on through the proxy - so DNS never leaks to the local network.
    /// </summary>
    public const string Dns4 = "1.1.1.1";

    public const int Mtu = 1500;

    private static readonly Cidr[] Lan4 =
    [
        Cidr.Parse("10.0.0.0/8"),
        Cidr.Parse("172.16.0.0/12"),
        Cidr.Parse("192.168.0.0/16"),
        Cidr.Parse("169.254.0.0/16"),
        Cidr.Parse("224.0.0.0/4"),
        Cidr.Parse("255.255.255.255/32"),
    ];

    private static readonly Cidr[] Lan6 =
    [
        Cidr.Parse("fc00::/7"),
        Cidr.Parse("fe80::/10"),
        Cidr.Parse("ff00::/8"),
    ];

    public List<Cidr> Routes4 { get; private init; } = [];
    public List<Cidr> Routes6 { get; private init; } = [];

    /// <summary>True: only <see cref="AllowedApps"/> use the VPN.</summary>
    public bool AppWhitelist { get; private init; }

    public List<string> AllowedApps { get; private init; } = [];

    /// <summary>Apps kept out of the VPN; always includes our own package (the core must dial out directly).</summary>
    public List<string> DisallowedApps { get; private init; } = [];

    public bool AnnounceDns { get; private init; }

    /// <summary>Entries of the subnet list that are not valid CIDRs (shown in the log).</summary>
    public List<string> InvalidSubnets { get; private init; } = [];

    public static TunnelPlan Build(KillSwitchItem? ks, string ownPackage)
    {
        ks ??= new KillSwitchItem();
        var whitelist = ks.SplitTunnelWhitelist;

        var subnets = new List<Cidr>();
        var invalid = new List<string>();
        foreach (var entry in ks.BypassSubnets ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }
            if (Cidr.TryParse(entry, out var cidr))
            {
                subnets.Add(cidr);
            }
            else
            {
                invalid.Add(entry.Trim());
            }
        }

        // Routes
        var onlyListed = whitelist && subnets.Count > 0;
        IEnumerable<Cidr> include4 = onlyListed ? subnets.Where(c => !c.IsV6) : new[] { Cidr.AnyV4 };
        IEnumerable<Cidr> include6 = onlyListed ? subnets.Where(c => c.IsV6) : new[] { Cidr.AnyV6 };

        var exclude = new List<Cidr>();
        if (!whitelist)
        {
            exclude.AddRange(subnets);
        }
        // Explicitly whitelisted subnets win over "allow LAN".
        if (ks.AllowLan && !onlyListed)
        {
            exclude.AddRange(Lan4);
            exclude.AddRange(Lan6);
        }

        var routes4 = Cidr.Subtract(include4, exclude.Where(c => !c.IsV6));
        var routes6 = Cidr.Subtract(include6, exclude.Where(c => c.IsV6));

        // Apps
        var apps = (ks.AndroidApps ?? [])
            .Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0 && a != ownPackage)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var appWhitelist = whitelist && apps.Count > 0;
        var disallowed = new List<string> { ownPackage };
        if (!whitelist)
        {
            disallowed.AddRange(apps);
        }

        var dns = Cidr.Parse(Dns4);
        return new TunnelPlan
        {
            Routes4 = routes4,
            Routes6 = routes6,
            AppWhitelist = appWhitelist,
            AllowedApps = appWhitelist ? apps : new List<string>(),
            DisallowedApps = appWhitelist ? new List<string>() : disallowed,
            AnnounceDns = routes4.Any(r => r.Contains(dns)),
            InvalidSubnets = invalid,
        };
    }

    /// <summary>One line for the log: what the tunnel covers.</summary>
    public string Describe()
    {
        var apps = AppWhitelist
            ? $"apps: only {AllowedApps.Count}"
            : DisallowedApps.Count > 1 ? $"apps: all except {DisallowedApps.Count - 1}" : "apps: all";
        return $"{apps}; routes v4 {Routes4.Count}, v6 {Routes6.Count}; dns {(AnnounceDns ? Dns4 : "system")}";
    }
}
