using Microsoft.Extensions.Logging;

namespace CodebaseGuardian.Watching;

/// <summary>
/// Reports working-tree edits in batches: a <see cref="FileSystemWatcher"/> on the repository root collects changed paths
/// and <paramref name="onChanged"/> is called once the tree has been quiet for the debounce period.
/// </summary>
internal sealed class WorkingTreeWatcher : IDisposable
{
    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules" };

    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly Func<IReadOnlyList<string>, Task> _onChanged;
    private readonly ILogger _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private HashSet<string> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    public WorkingTreeWatcher(string root, TimeSpan debounce, Func<IReadOnlyList<string>, Task> onChanged, ILogger logger)
    {
        _root = Path.GetFullPath(root);
        _debounce = debounce;
        _onChanged = onChanged;
        _logger = logger;
        _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Record(e.FullPath);
        _watcher.Changed += (_, e) => Record(e.FullPath);
        _watcher.Deleted += (_, e) => Record(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Record(e.OldFullPath);
            Record(e.FullPath);
        };
        _watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "The working-tree watcher reported an error; some changes may have been missed.");
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    /// <summary>Repository-relative path with '/' separators, or null when it is outside the root or inside an ignored directory.</summary>
    internal static string? ToRelativePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative is "." or "" || relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
        {
            return null;
        }

        return relative.Split('/').Any(IgnoredSegments.Contains) ? null : relative;
    }

    private void Record(string fullPath)
    {
        try
        {
            if (ToRelativePath(_root, fullPath) is not { } relative || Directory.Exists(fullPath))
            {
                return; // a directory's own timestamp changes whenever a file in it does; the file is reported itself
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _pending.Add(relative);
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record a working-tree change.");
        }
    }

    private async Task FlushAsync()
    {
        IReadOnlyList<string> batch;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending.Order(StringComparer.Ordinal)];
            _pending = new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            await _onChanged(batch);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Reporting working-tree changes failed.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _watcher.Dispose();
        _timer.Dispose();
    }
}
