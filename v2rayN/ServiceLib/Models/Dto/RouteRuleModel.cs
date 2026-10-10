namespace ServiceLib.Models.Dto;

/// <summary>
/// One rule card on the Secure Connect "Routes" page: name, condition lines,
/// action and the enable switch, built from a stock v2rayN <see cref="RulesItem"/>.
/// </summary>
public partial class RouteRuleModel : ReactiveObject
{
    private const int MaxLines = 4;

    public RouteRuleModel(RulesItem rule, int index)
    {
        Rule = rule;
        Title = rule.Remarks.IsNullOrEmpty() ? string.Format(ResUI.ScRuleTitle, index + 1) : rule.Remarks;
        Summary = BuildSummary(rule);
        Enabled = rule.Enabled;

        var outbound = rule.OutboundTag.IsNullOrEmpty() ? Global.ProxyTag : rule.OutboundTag;
        IsDirect = outbound == Global.DirectTag;
        IsBlock = outbound == Global.BlockTag;
        IsProxy = !IsDirect && !IsBlock;
        ActionText = IsDirect ? ResUI.ScActionBypass
            : IsBlock ? ResUI.ScActionBlock
            : outbound == Global.ProxyTag ? ResUI.ScActionProxy
            : string.Format(ResUI.ScActionProxyTo, outbound);
        if (rule.RuleType == ERuleType.DNS)
        {
            ActionText += ResUI.ScDnsOnly;
        }
        else if (rule.RuleType == ERuleType.Routing)
        {
            ActionText += ResUI.ScRoutingOnly;
        }
    }

    public RulesItem Rule { get; }

    public string Id => Rule.Id;

    public string Title { get; }

    public string Summary { get; }

    public string ActionText { get; }

    public bool IsProxy { get; }

    public bool IsDirect { get; }

    public bool IsBlock { get; }

    [Reactive]
    public partial bool Enabled { get; set; }

    /// <summary>Condition lines like NekoBox: domains, IPs, protocol, apps, ports.</summary>
    public static string BuildSummary(RulesItem rule)
    {
        var lines = new List<string>();
        lines.AddRange(rule.Domain?.Where(t => t.IsNotEmpty()) ?? []);
        lines.AddRange(rule.Ip?.Where(t => t.IsNotEmpty()) ?? []);
        if (rule.Protocol?.Count > 0)
        {
            lines.Add(string.Format(ResUI.ScSumProtocol, string.Join(", ", rule.Protocol)));
        }
        if (rule.Process?.Count > 0)
        {
            lines.Add(rule.Process.Count == 1
                ? string.Format(ResUI.ScSumApp, rule.Process[0])
                : string.Format(ResUI.ScSumApps, rule.Process.Count));
        }
        if (rule.Port.IsNotEmpty())
        {
            lines.Add(string.Format(ResUI.ScSumPort, rule.Port));
        }
        if (rule.Network.IsNotEmpty())
        {
            lines.Add(string.Format(ResUI.ScSumNetwork, rule.Network));
        }
        if (rule.InboundTag?.Count > 0)
        {
            lines.Add(string.Format(ResUI.ScSumInbound, string.Join(", ", rule.InboundTag)));
        }

        if (lines.Count == 0)
        {
            return ResUI.ScSumAnyTraffic;
        }
        if (lines.Count > MaxLines)
        {
            lines = [.. lines.Take(MaxLines - 1), "…"];
        }
        return string.Join(Environment.NewLine, lines);
    }
}
