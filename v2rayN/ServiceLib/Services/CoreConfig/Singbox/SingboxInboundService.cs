namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenInbounds()
    {
        try
        {
            var listen = "0.0.0.0";
            var listenPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            var isUsingLocalMixedPort = _node.Address == Global.Loopback && _node.Port == listenPort;
            _coreConfig.inbounds = [];

            if (!context.IsTunEnabled || !isUsingLocalMixedPort)
            {
                var inbound = new Inbound4Sbox()
                {
                    type = nameof(EInboundProtocol.mixed),
                    tag = nameof(EInboundProtocol.socks),
                    listen = Global.Loopback,
                };
                _coreConfig.inbounds.Add(inbound);

                var inboundConf = _config.Inbound.First();
                inbound.listen_port = listenPort;

                if (inboundConf.SecondLocalPortEnabled)
                {
                    var inbound2 = BuildInbound(inbound, EInboundProtocol.socks2, true);
                    _coreConfig.inbounds.Add(inbound2);
                }

                if (inboundConf.AllowLANConn)
                {
                    if (inboundConf.NewPort4LAN)
                    {
                        var inbound3 = BuildInbound(inbound, EInboundProtocol.socks3, true);
                        inbound3.listen = listen;
                        _coreConfig.inbounds.Add(inbound3);

                        //auth
                        if (inboundConf.User.IsNotEmpty() && inboundConf.Pass.IsNotEmpty())
                        {
                            inbound3.users = new() { new() { username = inboundConf.User, password = inboundConf.Pass } };
                        }
                    }
                    else
                    {
                        inbound.listen = listen;
                    }
                }
            }

            if (context.IsTunEnabled)
            {
                if (_config.TunModeItem.Mtu <= 0)
                {
                    _config.TunModeItem.Mtu = Global.TunMtus.First();
                }
                if (_config.TunModeItem.Stack.IsNullOrEmpty())
                {
                    _config.TunModeItem.Stack = Global.TunStacks.First();
                }

                var tunInbound = JsonUtils.Deserialize<Inbound4Sbox>(EmbedUtils.GetEmbedText(Global.TunSingboxInboundFileName)) ?? new Inbound4Sbox { };
                tunInbound.interface_name = context.IsMacOS ? null : "singbox_tun";
                tunInbound.mtu = _config.TunModeItem.Mtu;
                tunInbound.auto_route = _config.TunModeItem.AutoRoute;
                tunInbound.strict_route = _config.TunModeItem.StrictRoute;
                tunInbound.stack = _config.TunModeItem.Stack;

                var address = _config.TunModeItem.IPv4Address.NullIfEmpty() ?? Global.TunIPv4Address.First();
                tunInbound.address = [address];
                // The TUN always gets an IPv6 address: without one the OS sends IPv6
                // (and DNS to an IPv6 resolver) straight out of the physical interface,
                // around the tunnel - macOS ignores strict_route, Windows only uses it
                // against DNS leaks. With "IPv6" off in the settings, IPv6 is rejected
                // in routing (GenRoutingTunIPv6Block), so apps fall back to IPv4 through
                // the tunnel at once.
                if (_config.TunModeItem.EnableIPv6Address == true || CanAddTunIPv6())
                {
                    var address6 = _config.TunModeItem.IPv6Address.NullIfEmpty() ?? Global.TunIPv6Address.First();
                    tunInbound.address.Add(address6);
                }
                tunInbound.route_exclude_address = _config.TunModeItem.RouteExcludeAddress;

                _coreConfig.inbounds.Add(tunInbound);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// False only on Linux with IPv6 turned off for new interfaces: an IPv6
    /// address could not be added there (and no IPv6 can leak either).
    /// </summary>
    private static bool CanAddTunIPv6()
    {
        if (!Utils.IsLinux())
        {
            return true;
        }
        try
        {
            const string path = "/proc/sys/net/ipv6/conf/default/disable_ipv6";
            return File.Exists(path) && File.ReadAllText(path).Trim() != "1";
        }
        catch
        {
            return false;
        }
    }

    private Inbound4Sbox BuildInbound(Inbound4Sbox inItem, EInboundProtocol protocol, bool bSocks)
    {
        var inbound = JsonUtils.DeepCopy(inItem);
        inbound.tag = protocol.ToString();
        inbound.listen_port = inItem.listen_port + (int)protocol;
        inbound.type = nameof(EInboundProtocol.mixed);
        return inbound;
    }
}
