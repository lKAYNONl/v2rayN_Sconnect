namespace ServiceLib.Handler;

/// <summary>
/// What one run of <see cref="SubscriptionHandler.UpdateProcess" /> did, for the
/// user-visible result: subscriptions whose profiles were replaced, those that
/// had nothing new, and those that failed (name, reason).
/// </summary>
public sealed class SubUpdateSummary
{
    public List<string> Updated { get; } = [];
    public List<string> Unchanged { get; } = [];
    public List<(string Name, string Reason)> Failed { get; } = [];
    public bool Any => Updated.Count + Unchanged.Count + Failed.Count > 0;
}

public static class SubscriptionHandler
{
    // One update at a time: a manual and a scheduled update of the same
    // subscription would delete and re-add its profiles concurrently.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    // Subscription id -> hash of the content imported last (this run of the app).
    // The same content again is not re-imported: that would re-create every
    // profile (new ids, lost test results) and reconnect for nothing.
    private static readonly Dictionary<string, string> _lastContent = new();

    // Failures already shown by the scheduled update (subscription name ->
    // reason): the same error is not shown again every interval.
    private static readonly Dictionary<string, string> _shownFailures = new();

    /// <summary>
    /// Set by the last <see cref="UpdateProcess" /> before its final callback: the
    /// connection parameters of the active profile changed (or it is gone), so a
    /// running session has to be restarted. False when only other profiles or
    /// names changed - then the session can stay up.
    /// </summary>
    public static bool ActiveProfileChanged { get; private set; }

    public static async Task<SubUpdateSummary> UpdateProcess(Config config, string subId, bool blProxy, Func<bool, string, Task> updateFunc)
    {
        await _gate.WaitAsync();
        try
        {
            return await UpdateProcessCore(config, subId, blProxy, updateFunc);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<SubUpdateSummary> UpdateProcessCore(Config config, string subId, bool blProxy, Func<bool, string, Task> updateFunc)
    {
        var summary = new SubUpdateSummary();
        ActiveProfileChanged = false;
        await updateFunc?.Invoke(false, ResUI.MsgUpdateSubscriptionStart);
        var subItem = await AppManager.Instance.SubItems();

        if (subItem is not { Count: > 0 })
        {
            await updateFunc?.Invoke(false, ResUI.MsgNoValidSubscription);
            return summary;
        }

        foreach (var item in subItem)
        {
            var name = item.Remarks.IsNullOrEmpty() ? item.Url : item.Remarks;
            try
            {
                if (!IsValidSubscription(item, subId))
                {
                    continue;
                }

                var hashCode = $"{item.Remarks}->";
                if (item.Enabled == false)
                {
                    await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSkipSubscriptionUpdate}");
                    continue;
                }

                // Create download handler
                List<string> downloadErrors = [];
                var downloadHandle = CreateDownloadHandler(item, hashCode, updateFunc, downloadErrors);
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartGettingSubscriptions}");

                // Get all subscription content (main subscription + additional subscriptions)
                var result = await DownloadAllSubscriptions(config, item, blProxy, downloadHandle);

                // Process download result
                var (outcome, reason) = await ProcessDownloadResult(config, item.Id, result, hashCode, updateFunc, downloadErrors);
                switch (outcome)
                {
                    case EOutcome.Updated:
                        summary.Updated.Add(name);
                        break;

                    case EOutcome.Unchanged:
                        summary.Unchanged.Add(name);
                        break;

                    default:
                        summary.Failed.Add((name, reason));
                        break;
                }

                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
            catch (Exception ex)
            {
                var hashCode = $"{item.Remarks}->";
                Logging.SaveLog("UpdateSubscription", ex);
                summary.Failed.Add((name, ex.Message));
                await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgFailedImportSubscription}: {ex.Message}");
                await updateFunc?.Invoke(false, "-------------------------------------------------------");
            }
        }

        await updateFunc?.Invoke(summary.Updated.Count > 0, $"{ResUI.MsgUpdateSubscriptionEnd}");
        return summary;
    }

    /// <summary>
    /// Shows the result of an update as a notification. A manual update always
    /// says what happened; a scheduled one only speaks up when profiles really
    /// changed or when a subscription fails with a new reason.
    /// </summary>
    public static void Notify(SubUpdateSummary summary, bool manual)
    {
        List<string> lines = [];
        if (summary.Updated.Count > 0)
        {
            lines.Add(string.Format(ResUI.ScSubUpdated, string.Join(", ", summary.Updated)));
        }
        foreach (var (name, reason) in summary.Failed)
        {
            lock (_shownFailures)
            {
                if (!manual && _shownFailures.TryGetValue(name, out var shown) && shown == reason)
                {
                    continue;
                }
                _shownFailures[name] = reason;
            }
            lines.Add(string.Format(ResUI.ScSubFailed, name, reason));
        }
        lock (_shownFailures)
        {
            foreach (var name in summary.Updated.Concat(summary.Unchanged))
            {
                _shownFailures.Remove(name);
            }
        }
        if (manual && lines.Count == 0)
        {
            lines.Add(summary.Any ? ResUI.ScSubNoChanges : ResUI.MsgNoValidSubscription);
        }
        if (lines.Count > 0)
        {
            NoticeManager.Instance.Enqueue(string.Join(Environment.NewLine, lines));
        }
    }

    private static bool IsValidSubscription(SubItem item, string subId)
    {
        var id = item.Id.TrimEx();
        var url = item.Url.TrimEx();

        if (id.IsNullOrEmpty() || url.IsNullOrEmpty())
        {
            return false;
        }

        if (subId.IsNotEmpty() && item.Id != subId)
        {
            return false;
        }

        if (!url.StartsWith(Global.HttpsProtocol) && !url.StartsWith(Global.HttpProtocol))
        {
            return false;
        }

        return true;
    }

    private static DownloadService CreateDownloadHandler(SubItem item, string hashCode, Func<bool, string, Task> updateFunc, List<string> errors)
    {
        if (!HttpRequestHeadersHelper.TryParse(item.RequestHeaders, out var requestHeaders))
        {
            throw new FormatException(ResUI.SubRequestHeadersInvalid);
        }

        var downloadHandle = new DownloadService
        {
            AcceptHeader = "*/*",
            RequestHeaders = requestHeaders
        };
        downloadHandle.Error += (sender2, args) =>
        {
            var message = args.GetException().Message;
            lock (errors)
            {
                errors.Add(message);
            }
            updateFunc?.Invoke(false, $"{hashCode}{message}");
        };
        return downloadHandle;
    }

    private static async Task<string> DownloadSubscriptionContent(DownloadService downloadHandle, string url, bool blProxy, string userAgent)
    {
        var result = await downloadHandle.TryDownloadString(url, blProxy, userAgent);

        // If download with proxy fails, try direct connection
        if (blProxy && result.IsNullOrEmpty())
        {
            result = await downloadHandle.TryDownloadString(url, false, userAgent);
        }

        return result ?? string.Empty;
    }

    private static async Task<string> DownloadAllSubscriptions(Config config, SubItem item, bool blProxy, DownloadService downloadHandle)
    {
        // Download main subscription content
        var result = await DownloadMainSubscription(config, item, blProxy, downloadHandle);

        // Process additional subscription links (if any)
        if (item.ConvertTarget.IsNullOrEmpty() && item.MoreUrl.TrimEx().IsNotEmpty())
        {
            result = await DownloadAdditionalSubscriptions(item, result, blProxy, downloadHandle);
        }

        return result;
    }

    private static async Task<string> DownloadMainSubscription(Config config, SubItem item, bool blProxy, DownloadService downloadHandle)
    {
        // Prepare subscription URL and download directly
        var url = Utils.GetPunycode(item.Url.TrimEx());

        // If conversion is needed
        if (item.ConvertTarget.IsNotEmpty())
        {
            var subConvertUrl = config.ConstItem.SubConvertUrl.IsNullOrEmpty()
                ? Global.SubConvertUrls.FirstOrDefault()
                : config.ConstItem.SubConvertUrl;

            url = string.Format(subConvertUrl!, Utils.UrlEncode(url));

            if (!url.Contains("target="))
            {
                url += $"&target={item.ConvertTarget}";
            }

            if (!url.Contains("config="))
            {
                url += $"&config={Global.SubConvertConfig.FirstOrDefault()}";
            }
        }

        // Download and return result directly
        return await DownloadSubscriptionContent(downloadHandle, url, blProxy, item.UserAgent);
    }

    private static async Task<string> DownloadAdditionalSubscriptions(SubItem item, string mainResult, bool blProxy, DownloadService downloadHandle)
    {
        var result = mainResult;

        // If main subscription result is Base64 encoded, decode it first
        if (result.IsNotEmpty() && Utils.IsBase64String(result))
        {
            result = Utils.Base64Decode(result);
        }

        // Process additional URL list
        var lstUrl = item.MoreUrl.TrimEx().Split(",") ?? [];
        foreach (var it in lstUrl)
        {
            var url2 = Utils.GetPunycode(it);
            if (url2.IsNullOrEmpty())
            {
                continue;
            }

            var additionalResult = await DownloadSubscriptionContent(downloadHandle, url2, blProxy, item.UserAgent);

            if (additionalResult.IsNotEmpty())
            {
                // Process additional subscription results, add to main result
                if (Utils.IsBase64String(additionalResult))
                {
                    result += Environment.NewLine + Utils.Base64Decode(additionalResult);
                }
                else
                {
                    result += Environment.NewLine + additionalResult;
                }
            }
        }

        return result;
    }

    private enum EOutcome
    {
        Updated,
        Unchanged,
        Failed,
    }

    private static async Task<(EOutcome, string)> ProcessDownloadResult(Config config, string id, string result, string hashCode, Func<bool, string, Task> updateFunc, List<string> downloadErrors)
    {
        if (result.IsNullOrEmpty())
        {
            await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgSubscriptionDecodingFailed}");
            return (EOutcome.Failed, DescribeDownloadError(downloadErrors));
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgGetSubscriptionSuccessfully}");

        // If result is too short, display content directly
        if (result.Length < 99)
        {
            await updateFunc?.Invoke(false, $"{hashCode}{result}");
        }

        // A web page is not a subscription (a wrong link, or the server sent this
        // client somewhere else). It must not get as far as the import: that
        // deletes the subscription's current profiles first.
        if (TryGetHtmlTitle(result, out var title))
        {
            var reason = string.Format(ResUI.ScSubHtml, title);
            Logging.SaveLog($"FailedImportSubscription: a web page instead of profiles ({title})");
            await updateFunc?.Invoke(false, $"{hashCode}{reason}");
            return (EOutcome.Failed, reason);
        }

        var hash = Utils.GetMd5(result);
        bool same;
        lock (_lastContent)
        {
            same = _lastContent.TryGetValue(id, out var last) && last == hash;
        }
        if (same && (await AppManager.Instance.ProfileItems(id))?.Count > 0)
        {
            await updateFunc?.Invoke(false, $"{hashCode}{ResUI.ScSubNoChanges}");
            return (EOutcome.Unchanged, string.Empty);
        }

        await updateFunc?.Invoke(false, $"{hashCode}{ResUI.MsgStartParsingSubscription}");

        // The active profile before the import, when it belongs to this subscription.
        var activeBefore = await AppManager.Instance.GetProfileItem(config.IndexId);
        if (activeBefore?.Subid != id)
        {
            activeBefore = null;
        }

        // Add servers to configuration
        var ret = await ConfigHandler.AddBatchServers(config, result, id, true);
        if (ret <= 0)
        {
            Logging.SaveLog("FailedImportSubscription");
            Logging.SaveLog(result.Length > 2000 ? result[..2000] + "..." : result);
        }
        else
        {
            lock (_lastContent)
            {
                _lastContent[id] = hash;
            }
        }
        if (activeBefore is not null)
        {
            // The import re-creates the profiles and re-selects the matching one.
            var activeAfter = await AppManager.Instance.GetProfileItem(config.IndexId);
            if (!ConfigHandler.IsSameServer(activeBefore, activeAfter))
            {
                ActiveProfileChanged = true;
            }
        }

        // Update completion message
        await updateFunc?.Invoke(false, ret > 0
                ? $"{hashCode}{ResUI.MsgUpdateSubscriptionEnd}"
                : $"{hashCode}{ResUI.MsgFailedImportSubscription}");

        return ret > 0 ? (EOutcome.Updated, string.Empty) : (EOutcome.Failed, ResUI.ScSubNoProfiles);
    }

    /// <summary>The reason a download gave nothing, in words a user can act on.</summary>
    private static string DescribeDownloadError(List<string> errors)
    {
        List<string> copy;
        lock (errors)
        {
            copy = [.. errors];
        }
        // "HTTP/0.0 307 Temporary Redirect": an answer .NET cannot read. It is the
        // most telling one, so it wins over the timeouts of the other attempts.
        var badAnswer = copy.LastOrDefault(t => t.Contains("invalid_response_status_line", StringComparison.OrdinalIgnoreCase));
        if (badAnswer is not null)
        {
            var status = badAnswer[(badAnswer.IndexOf(',') + 1)..].Trim();
            return string.Format(ResUI.ScSubBadAnswer, status);
        }
        var lastError = copy.LastOrDefault(t => t.IsNotEmpty());
        if (lastError.IsNullOrEmpty())
        {
            return ResUI.ScSubEmpty;
        }
        // .NET reports these by resource key in this build.
        if (lastError.Contains("timedout", StringComparison.OrdinalIgnoreCase)
            || lastError.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase))
        {
            return ResUI.ScSubTimeout;
        }
        return lastError;
    }

    /// <summary>True when the content is an HTML page; its title, if it has one.</summary>
    private static bool TryGetHtmlTitle(string content, out string title)
    {
        title = string.Empty;
        var head = content.AsSpan(0, Math.Min(content.Length, 2000)).TrimStart().ToString();
        if (!head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            && !head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var start = content.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start = content.IndexOf('>', start) + 1;
            var end = content.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
            if (start > 0 && end > start)
            {
                title = System.Net.WebUtility.HtmlDecode(content[start..end]).Trim();
                if (title.Length > 80)
                {
                    title = title[..80] + "…";
                }
            }
        }
        if (title.IsNullOrEmpty())
        {
            title = "HTML";
        }
        return true;
    }
}
