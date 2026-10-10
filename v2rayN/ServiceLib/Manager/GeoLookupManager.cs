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
