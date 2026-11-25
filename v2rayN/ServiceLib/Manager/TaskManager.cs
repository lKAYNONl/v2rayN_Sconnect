namespace ServiceLib.Manager;

public class TaskManager
{
    private static readonly Lazy<TaskManager> _instance = new(() => new());
    public static TaskManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;

    public void RegUpdateTask(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        Task.Run(ScheduledTasks);
    }

    private async Task ScheduledTasks()
    {
        Logging.SaveLog("Setup Scheduled Tasks");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var numOfExecuted = 1;
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            //Execute once 1 minute
            try
            {
                await UpdateTaskRunSubscription();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ScheduledTasks - UpdateTaskRunSubscription", ex);
            }

            //Execute once 20 minute
            if (numOfExecuted % 20 == 0)
            {
                //Logging.SaveLog("Execute save config");

                try
                {
                    await ConfigHandler.SaveConfig(_config);
                    await ProfileExManager.Instance.SaveTo();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - SaveConfig", ex);
                }
            }

            //Execute once 1 hour
            if (numOfExecuted % 60 == 0)
            {
                //Logging.SaveLog("Execute delete expired files");

                FileUtils.DeleteExpiredFiles(Utils.GetBinConfigPath(), DateTime.Now.AddHours(-1), "Test");
                FileUtils.DeleteExpiredFiles(Utils.GetLogPath(), DateTime.Now.AddDays(-7));
                FileUtils.DeleteExpiredFiles(Utils.GetTempPath(), DateTime.Now.AddDays(-7));

                try
                {
                    await UpdateTaskRunGeo(numOfExecuted / 60);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunGeo", ex);
                }
            }

            // Secure Connect: no automatic daily update check (it would offer
            // upstream v2rayN builds). Cores and geo files are still updated
            // on demand from "Check update" and by the geo auto-update interval.
            numOfExecuted++;
        }
    }

    private async Task UpdateTaskRunSubscription()
    {
        // A subscription's own interval wins; the others use the app-wide one
        // (Secure Connect: on by default, so server-side changes arrive by themselves).
        var defaultInterval = Math.Max(0, _config.KillSwitchItem?.SubAutoUpdateMinutes ?? 0);
        var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
        var lstSubs = (await AppManager.Instance.SubItems())?
            .Where(t => t.Enabled && t.Url.IsNotEmpty())
            .Select(t => (Item: t, Interval: t.AutoUpdateInterval > 0 ? t.AutoUpdateInterval : defaultInterval))
            .Where(t => t.Interval > 0 && updateTime - t.Item.UpdateTime >= t.Interval * 60)
            .Select(t => t.Item)
            .ToList();

        if (lstSubs is not { Count: > 0 })
        {
            return;
        }

        Logging.SaveLog("Execute update subscription");

        foreach (var item in lstSubs)
        {
            var summary = await SubscriptionHandler.UpdateProcess(_config, item.Id, true, async (success, msg) =>
            {
                await _updateFunc?.Invoke(success, msg);
                if (success)
                {
                    Logging.SaveLog($"Update subscription end. {msg}");
                }
            });
            SubscriptionHandler.Notify(summary, false);
            foreach (var (name, reason) in summary.Failed)
            {
                Logging.SaveLog($"Update subscription failed. {name}: {reason}");
            }
            // The time of the attempt, in the stored item.
            var stored = await AppManager.Instance.GetSubItem(item.Id);
            if (stored is not null)
            {
                stored.UpdateTime = updateTime;
                await ConfigHandler.AddSubItem(_config, stored);
            }
            await Task.Delay(1000);
        }
    }

    private async Task UpdateTaskRunGeo(int hours)
    {
        if (_config.GuiItem.AutoUpdateInterval > 0 && hours > 0 && hours % _config.GuiItem.AutoUpdateInterval == 0)
        {
            Logging.SaveLog("Execute update geo files");

            await new UpdateService(_config, async (success, msg) =>
            {
                await _updateFunc?.Invoke(false, msg);
            }).UpdateGeoFileAll();
        }
    }
}
