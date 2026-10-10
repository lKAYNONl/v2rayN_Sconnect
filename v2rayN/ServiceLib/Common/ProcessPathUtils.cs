namespace ServiceLib.Common;

/// <summary>
/// Split tunneling by program: lists running programs for the picker and turns
/// "whole folder" entries into core rules / firewall allow-lists.
/// A bypass entry can be an exe name ("telegram.exe"), a full path
/// ("C:\Games\game.exe") or a folder ("C:\Games\" - every program under it,
/// sub-folders included).
/// </summary>
public static class ProcessPathUtils
{
    /// <summary>A running program: exe file name and full image path.</summary>
    public record RunningProgram(string Name, string Path);

    /// <summary>Upper bound of programs a folder entry expands to for the firewall.</summary>
    public const int MaxFirewallPrograms = 150;

    /// <summary>
    /// Distinct running programs (by image path), sorted by name. Programs whose
    /// path cannot be read (protected/system processes) are skipped.
    /// </summary>
    public static List<RunningProgram> GetRunningPrograms()
    {
        var result = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id is 0 or 4)
                    {
                        continue; // Idle, System
                    }
                    var path = GetProcessPath(process);
                    if (path.IsNullOrEmpty() || result.ContainsKey(path))
                    {
                        continue;
                    }
                    result[path] = new RunningProgram(System.IO.Path.GetFileName(path), path);
                }
                catch
                {
                    // the process exited or is not accessible - skip it
                }
            }
        }
        return result.Values
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True when the entry names a folder: it ends with a path separator, or it
    /// is an existing directory that is not an .exe.
    /// </summary>
    public static bool IsFolderEntry(string? entry)
    {
        if (entry.IsNullOrEmpty())
        {
            return false;
        }
        if (entry.EndsWith('\\') || entry.EndsWith('/'))
        {
            return true;
        }
        if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            return Directory.Exists(entry);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Folder path with native separators and exactly one trailing separator.</summary>
    public static string NormalizeFolder(string folder)
    {
        var sep = OperatingSystem.IsWindows() ? '\\' : '/';
        var dir = folder.Trim().Trim('"');
        if (OperatingSystem.IsWindows())
        {
            dir = dir.Replace('/', '\\');
        }
        return dir.TrimEnd('\\', '/') + sep;
    }

    /// <summary>
    /// Case-insensitive RE2 (Go) regex that matches every program inside the
    /// folder, for sing-box "process_path_regex".
    /// </summary>
    public static string FolderToPathRegex(string folder)
    {
        const string special = "\\.+*?()|[]{}^$";
        var sb = new StringBuilder("(?i)^");
        foreach (var c in NormalizeFolder(folder))
        {
            if (special.Contains(c))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        sb.Append(".*");
        return sb.ToString();
    }

    /// <summary>
    /// Concrete program paths for the firewall allow-list (the Windows Firewall
    /// matches full paths only): full paths as-is, folders expanded to the .exe
    /// files inside them, bare exe names resolved from running processes.
    /// Capped at <see cref="MaxFirewallPrograms"/>.
    /// </summary>
    public static List<string> ExpandForFirewall(IEnumerable<string>? entries)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<RunningProgram>? running = null;

        void Add(string path)
        {
            if (result.Count < MaxFirewallPrograms && seen.Add(path))
            {
                result.Add(path);
            }
        }

        foreach (var raw in entries ?? [])
        {
            var entry = raw?.Trim().Trim('"');
            if (entry.IsNullOrEmpty())
            {
                continue;
            }

            if (IsFolderEntry(entry))
            {
                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    };
                    foreach (var exe in Directory.EnumerateFiles(NormalizeFolder(entry), "*.exe", options))
                    {
                        if (result.Count >= MaxFirewallPrograms)
                        {
                            break;
                        }
                        Add(exe);
                    }
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ExpandForFirewall", ex);
                }
            }
            else if (entry.Contains('\\') || entry.Contains('/'))
            {
                Add(entry);
            }
            else
            {
                running ??= GetRunningPrograms();
                var name = Utils.GetExeName(entry);
                foreach (var program in running.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(program.Path);
                }
            }
        }

        if (result.Count >= MaxFirewallPrograms)
        {
            Logging.SaveLog($"Kill switch: bypass programs capped at {MaxFirewallPrograms}");
        }
        return result;
    }

    private static string? GetProcessPath(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        // Limited query rights work for most processes, including elevated ones,
        // where Process.MainModule throws.
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
