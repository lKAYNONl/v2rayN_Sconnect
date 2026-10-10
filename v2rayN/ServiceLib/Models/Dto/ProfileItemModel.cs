namespace ServiceLib.Models.Dto;

[Serializable]
public partial class ProfileItemModel : ReactiveObject
{
    public bool IsActive { get; set; }
    public string IndexId { get; set; }
    public EConfigType ConfigType { get; set; }
    public string Remarks { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public string Network { get; set; }
    public string StreamSecurity { get; set; }
    public string Subid { get; set; }
    public string SubRemarks { get; set; }
    public int Sort { get; set; }

    [Reactive]
    public partial int Delay { get; set; }

    public decimal Speed { get; set; }

    [Reactive]
    public partial string DelayVal { get; set; }

    // 2-letter country code of the server, resolved lazily for the card badge.
    [Reactive]
    public partial string CountryCode { get; set; }

    [Reactive]
    public partial string SpeedVal { get; set; }

    [Reactive]
    public partial string IpInfo { get; set; }

    [Reactive]
    public partial string TodayUp { get; set; }

    [Reactive]
    public partial string TodayDown { get; set; }

    [Reactive]
    public partial string TotalUp { get; set; }

    [Reactive]
    public partial string TotalDown { get; set; }

    /// <summary>Protocol summary for the dashboard card sub-line, e.g. "VLESS · tcp · reality".</summary>
    public string ProtocolInfo
    {
        get
        {
            var parts = new List<string> { ConfigType.ToString() };
            if (!Network.IsNullOrEmpty())
            {
                parts.Add(Network);
            }
            if (!StreamSecurity.IsNullOrEmpty())
            {
                parts.Add(StreamSecurity);
            }
            return string.Join(" · ", parts);
        }
    }

    public string GetSummary()
    {
        var summary = $"[{ConfigType}] {Remarks}";
        if (!ConfigType.IsComplexType())
        {
            summary += $"({Address}:{Port})";
        }

        return summary;
    }
}
