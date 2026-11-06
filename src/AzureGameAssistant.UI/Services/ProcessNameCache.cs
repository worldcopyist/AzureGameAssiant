using System.Diagnostics;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Resolves process names for the shared-memory PIDs. Names never enter the
/// telemetry map, so the cache lives in the UI process and refreshes lazily.
/// </summary>
public sealed class ProcessNameCache
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<uint, Entry> _entries = new();

    public string GetName(uint pid)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        DateTime now = DateTime.UtcNow;
        if (_entries.TryGetValue(pid, out Entry entry) && now - entry.Refreshed < RefreshInterval)
        {
            return entry.Name;
        }

        string name = Resolve(pid);
        _entries[pid] = new Entry(name, now);

        if (_entries.Count > 512)
        {
            Prune(now);
        }

        return name;
    }

    private static string Resolve(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private void Prune(DateTime now)
    {
        var stale = new List<uint>();
        foreach ((uint pid, Entry entry) in _entries)
        {
            if (now - entry.Refreshed > TimeSpan.FromSeconds(30))
            {
                stale.Add(pid);
            }
        }

        foreach (uint pid in stale)
        {
            _entries.Remove(pid);
        }
    }

    private readonly record struct Entry(string Name, DateTime Refreshed);
}
