namespace ServiceLib.Manager;

/// <summary>
/// Best-effort geolocation of a server address to a 2-letter country code for
/// the Secure Connect profile cards. Resolves a host to an IP and queries a
/// public geo API (ip-api.com), caching per host. Every failure returns an
/// empty string - it never throws and never blocks the UI.
/// </summary>
public class GeoLookupManager
{
    private static readonly Lazy<GeoLookupManager> _instance = new(() => new());
    public static GeoLookupManager Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _throttle = new(3, 3);

    /// <summary>Cached country code for a host, if it was already resolved.</summary>
    public bool TryGetCached(string? address, out string code)
    {
        code = string.Empty;
        var host = address?.Trim();
        if (host.IsNullOrEmpty() || !_cache.TryGetValue(host, out var cached) || cached.IsNullOrEmpty())
        {
            return false;
        }
        code = cached;
        return true;
    }

    /// <summary>
    /// Public (egress) IP as the internet sees it - shown in the dashboard's IP
    /// chip. Through the local SOCKS inbound while connected (so it is the
    /// tunnel's exit IP), directly otherwise (the machine's real IP). Returns an
    /// empty string on any failure.
    /// </summary>
    public async Task<string> GetPublicIpAsync(bool viaProxy)
    {
        try
        {
            IWebProxy? proxy = viaProxy
                ? new WebProxy($"socks5://{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}")
                : null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var json = await new DownloadService().TryDownloadString("http://ip-api.com/json/?fields=status,query,countryCode", proxy, "", cts.Token);
            if (json.IsNullOrEmpty())
            {
                return string.Empty;
            }
            var info = JsonUtils.Deserialize<IPAPIInfo>(json);
            return info?.query ?? info?.ip ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<string> GetCountryCodeAsync(string? address)
    {
        var host = address?.Trim();
        if (host.IsNullOrEmpty())
        {
            return string.Empty;
        }
        if (_cache.TryGetValue(host, out var cached))
        {
            return cached;
        }

        await _throttle.WaitAsync();
        try
        {
            // Resolve a hostname to an IP; ip-api can also take a name directly,
            // so a failed resolve just falls back to the original host.
            var ip = host;
            if (!IPAddress.TryParse(host, out _))
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host);
                    ip = addrs.FirstOrDefault()?.ToString() ?? host;
                }
                catch
                {
                    // ignore - fall back to the host string
                }
            }

            var url = $"http://ip-api.com/json/{ip}?fields=status,countryCode";
            var json = await new DownloadService().TryDownloadString(url, (IWebProxy?)null, "", default);
            var code = string.Empty;
            if (!json.IsNullOrEmpty())
            {
                var info = JsonUtils.Deserialize<IPAPIInfo>(json);
                code = (info?.countryCode ?? info?.country_code ?? string.Empty).ToUpperInvariant();
            }

            _cache[host] = code;
            return code;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            _throttle.Release();
        }
    }
}
