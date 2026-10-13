using ServiceLib.Handler.Builder;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Headless mode for the Steam Deck (Decky Loader) plugin. It uses the same
/// profiles, routing and split tunneling as the app, without a window:
/// <code>
///   v2rayN --decky list              profiles and the active one
///   v2rayN --decky config &lt;id&gt;       sing-box TUN config for that profile
/// </code>
/// The answer is one JSON line on stdout: <c>{"ok":true,...}</c> or
/// <c>{"ok":false,"error":"..."}</c>. The plugin runs sing-box itself (as root),
/// so this mode never starts a core.
/// </summary>
internal static class DeckyCli
{
    public const string Flag = "--decky";

    public static int Run(string[] args)
    {
        object answer;
        int code;
        try
        {
            (answer, code) = RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("DeckyCli", ex);
            (answer, code) = (Fail(ex.Message), 1);
        }
        Console.Out.WriteLine(JsonUtils.Serialize(answer, false));
        Console.Out.Flush();
        return code;
    }

    private static object Fail(string error) => new { ok = false, error };

    private static async Task<(object, int)> RunAsync(string[] args)
    {
        if (!AppManager.Instance.InitApp())
        {
            return (Fail("Cannot load the v2rayN settings (guiConfigs)."), 1);
        }
        AppManager.Instance.InitComponents();
        var config = AppManager.Instance.Config;
        // What the app sets up on its first start (no-ops afterwards).
        await ConfigHandler.InitBuiltinRouting(config);
        await ConfigHandler.InitBuiltinDNS(config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(config);
        await ProfileExManager.Instance.Init();
        // Copies the cores to the data folder when the app folder is read-only
        // (as in the Decky plugin) and makes them executable.
        await CoreManager.Instance.Init(config, (_, _) => Task.CompletedTask);

        var command = args.Length > 0 ? args[0] : string.Empty;
        return command switch
        {
            "list" => await ListAsync(config),
            "config" when args.Length > 1 => await ConfigAsync(config, args[1]),
            _ => (Fail($"Usage: {Flag} list | {Flag} config <profile id>"), 2),
        };
    }

    private static async Task<(object, int)> ListAsync(Config config)
    {
        // Plain entities (no UI models): this mode runs without the app's UI setup.
        var items = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var groups = (await AppManager.Instance.SubItems() ?? [])
            .Where(t => !t.Id.IsNullOrEmpty())
            .GroupBy(t => t.Id)
            .ToDictionary(g => g.Key, g => g.First().Remarks ?? string.Empty);
        var profiles = items
            .Where(t => t.ConfigType != EConfigType.Custom)   // custom configs are for a specific core
            .Select(t => new
            {
                id = t.IndexId,
                name = t.Remarks,
                type = t.ConfigType.ToString(),
                address = t.Address,
                port = t.Port,
                group = !t.Subid.IsNullOrEmpty() && groups.TryGetValue(t.Subid, out var group) ? group : string.Empty,
                sort = ProfileExManager.Instance.GetSort(t.IndexId),
            })
            .OrderBy(t => t.sort)
            .ToList();
        return (new { ok = true, active = config.IndexId ?? string.Empty, profiles }, 0);
    }

    private static async Task<(object, int)> ConfigAsync(Config config, string indexId)
    {
        var node = await AppManager.Instance.GetProfileItem(indexId);
        if (node is null)
        {
            return (Fail($"Profile not found: {indexId}"), 1);
        }
        if (node.ConfigType == EConfigType.Custom)
        {
            return (Fail("Custom configs cannot be run with sing-box here."), 1);
        }

        // The plugin always runs sing-box in TUN mode (the whole Deck through the
        // tunnel). A copy of the settings, so the app's own TUN switch and the
        // profile's core type stay as they are.
        var appConfig = JsonUtils.DeepCopy(config) ?? config;
        appConfig.TunModeItem.EnableTun = true;
        node.CoreType = ECoreType.sing_box;

        var built = await CoreConfigContextBuilder.Build(appConfig, node);
        if (!built.Success)
        {
            return (Fail(string.Join("; ", built.ValidatorResult.Errors)), 1);
        }

        var file = Utils.GetBinConfigPath("decky.json");
        var result = await CoreConfigHandler.GenerateClientConfig(built.Context, file);
        if (result.Success != true)
        {
            return (Fail(result.Msg ?? "sing-box config could not be generated."), 1);
        }

        // Picking a profile in the plugin makes it the active one in the app too.
        await ConfigHandler.SetDefaultServerIndex(config, indexId);

        return (new
        {
            ok = true,
            id = node.IndexId,
            name = node.Remarks,
            config = file,
            cwd = Utils.GetBinConfigPath(),
            warnings = built.ValidatorResult.Warnings,
        }, 0);
    }
}
