namespace ServiceLib.ViewModels;

/// <summary>
/// Secure Connect "Routes" page, NekoBox style: the rules of the active routing
/// set as cards that can be switched on/off, duplicated, edited, reordered and
/// deleted. Rules live in the stock v2rayN routing set (RoutingItem.RuleSet),
/// so they apply to both sing-box and Xray; every change is saved and applied
/// to a running session.
/// </summary>
public partial class RoutesViewModel : MyReactiveObject
{
    private readonly Func<Task> _applyAsync;
    private RoutingItem? _routing;
    private List<RulesItem> _rules = [];

    public RoutesViewModel(Func<Task> applyAsync)
    {
        _config = AppManager.Instance.Config;
        _applyAsync = applyAsync;
        RoutingName = string.Empty;
    }

    public BulkObservableCollection<RouteRuleModel> Rules { get; } = [];

    /// <summary>Name of the routing set being edited.</summary>
    [Reactive]
    public partial string RoutingName { get; set; }

    [Reactive]
    public partial int RuleCount { get; set; }

    /// <summary>Loads a routing set (the active one when <paramref name="routingId"/> is empty).</summary>
    public async Task LoadAsync(string? routingId = null)
    {
        RoutingItem? routing = null;
        if (routingId.IsNotEmpty())
        {
            routing = await AppManager.Instance.GetRoutingItem(routingId);
        }
        routing ??= await ConfigHandler.GetDefaultRouting(_config);
        _routing = routing;

        _rules = routing?.RuleSet.IsNullOrEmpty() == false
            ? JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? []
            : [];
        foreach (var rule in _rules.Where(r => r.Id.IsNullOrEmpty()))
        {
            rule.Id = Utils.GetGuid(false);
        }

        RoutingName = routing?.Remarks ?? "-";
        Rebuild();
    }

    /// <summary>A copy of a rule for the editor (null if it no longer exists).</summary>
    public RulesItem? GetRuleCopy(string? id)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == id);
        return rule is null ? null : JsonUtils.DeepCopy(rule);
    }

    /// <summary>Index of a rule in the set (-1 if missing).</summary>
    public int IndexOf(string? id) => _rules.FindIndex(r => r.Id == id);

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == id);
        if (rule is null || rule.Enabled == enabled)
        {
            return;
        }
        rule.Enabled = enabled;
        var card = Rules.FirstOrDefault(c => c.Id == id);
        if (card != null)
        {
            card.Enabled = enabled;
        }
        await SaveAsync();
    }

    public async Task DuplicateAsync(string id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return;
        }
        var copy = JsonUtils.DeepCopy(_rules[index]);
        if (copy is null)
        {
            return;
        }
        copy.Id = Utils.GetGuid(false);
        var source = _rules[index].Remarks.IsNullOrEmpty() ? string.Format(ResUI.ScRuleTitle, index + 1) : _rules[index].Remarks;
        copy.Remarks = string.Format(ResUI.ScCopySuffix, source);
        _rules.Insert(index + 1, copy);
        Rebuild();
        await SaveAsync();
    }

    public async Task DeleteAsync(string id)
    {
        if (_rules.RemoveAll(r => r.Id == id) == 0)
        {
            return;
        }
        Rebuild();
        await SaveAsync();
    }

    /// <summary>Moves a rule up (-1) or down (+1); rules are matched top to bottom.</summary>
    public async Task MoveAsync(string id, int delta)
    {
        var index = IndexOf(id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rules.Count)
        {
            return;
        }
        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        Rebuild();
        await SaveAsync();
    }

    /// <summary>
    /// Saves an edited rule in place, or adds a new one at the top - the first
    /// matching rule wins, and sets usually end with a catch-all rule.
    /// </summary>
    public async Task UpsertAsync(RulesItem rule)
    {
        if (rule.Id.IsNullOrEmpty())
        {
            rule.Id = Utils.GetGuid(false);
        }
        var index = IndexOf(rule.Id);
        if (index >= 0)
        {
            _rules[index] = rule;
        }
        else
        {
            _rules.Insert(0, rule);
        }
        Rebuild();
        await SaveAsync();
    }

    private void Rebuild()
    {
        Rules.ReplaceRange(_rules.Select((r, i) => new RouteRuleModel(r, i)).ToList());
        RuleCount = _rules.Count;
    }

    private async Task SaveAsync()
    {
        if (_routing is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.ScNoRoutingSet);
            return;
        }

        _routing.RuleSet = JsonUtils.Serialize(_rules, false);
        _routing.RuleNum = _rules.Count;
        if (await ConfigHandler.SaveRoutingItem(_config, _routing) != 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        await _applyAsync();
    }
}
