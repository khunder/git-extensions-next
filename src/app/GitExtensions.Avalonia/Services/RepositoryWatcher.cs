namespace GitExtensions.Desktop.Services;

/// <summary>What part of the repository a file system change can affect.</summary>
[Flags]
public enum RepositoryChanges
{
    None = 0,

    /// <summary>The working tree or the index changed: the status (changed files) must be read again.</summary>
    WorkTree = 1,

    /// <summary>HEAD, a branch, a tag or the repository config changed: branches and history must be read again.</summary>
    Refs = 2,

    All = WorkTree | Refs,
}

/// <summary>
///  A burst of file system changes, reported once after the burst settled.
/// </summary>
/// <param name="Changes">What may have changed.</param>
/// <param name="WorkTreePaths">
///  The working tree paths that changed, relative to the working directory with '/' separators,
///  or <see langword="null"/> when they are not known (too many, or the watcher lost events).
/// </param>
public sealed record RepositoryChangeBatch(RepositoryChanges Changes, IReadOnlyCollection<string>? WorkTreePaths);

/// <summary>
///  Watches the working tree and the git directory of one repository and reports changes made outside the app
///  (an editor, a terminal, a build, a commit or checkout from the command line), so the UI can refresh itself
///  without F5. Events are filtered (git lock files, objects, logs), sorted into working tree and ref changes, and
///  debounced: a burst (a build, a checkout, <c>npm install</c>) becomes one report.
/// </summary>
/// <remarks>
///  <para>
///   On Windows <see cref="FileSystemWatcher"/> uses one recursive handle per directory; its buffer can overflow when
///   many files change at once, which is reported as a change of everything with unknown paths.
///   On Linux it uses inotify, one watch per directory; a large tree can exceed <c>fs.inotify.max_user_watches</c>.
///   Network drives and WSL paths (<c>\\wsl$\...</c>) do not deliver events reliably.
///   In those cases <see cref="IsReliable"/> is false and the owner should fall back to refreshing on window focus
///   and on a timer; <see cref="Problem"/> says why.
///  </para>
///  <para>The callback runs on a thread pool thread.</para>
/// </remarks>
public sealed class RepositoryWatcher : IDisposable
{
    /// <summary>How long the tree must stay quiet before a burst of changes is reported.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>A burst that never settles (a long build) is still reported this often.</summary>
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(2);

    /// <summary>More changed paths than this in one burst are reported as "unknown paths".</summary>
    public const int MaxReportedPaths = 500;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // Git directory entries that are written by operations which change HEAD or the working tree state.
    private static readonly string[] StateFiles =
    [
        "HEAD", "ORIG_HEAD", "MERGE_HEAD", "MERGE_MSG", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "AUTO_MERGE",
    ];

    private static readonly string[] StateDirectories = ["rebase-merge/", "rebase-apply/", "sequencer/"];

    // Git directory content that never changes what the UI shows (or that would make the app loop on its own git calls).
    private static readonly string[] IgnoredGitDirectories = ["objects/", "logs/", "hooks/", "lfs/", "modules/", "worktrees/", "fsmonitor--daemon/"];

    private readonly string _workingDir;
    private readonly string[] _gitDirs;
    private readonly Action<RepositoryChangeBatch> _changed;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _maxDelay;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();
    private readonly Timer _timer;

    private RepositoryChanges _pending;
    private HashSet<string>? _pendingPaths = NewPathSet();
    private DateTime _burstStart;
    private bool _disposed;

    /// <summary>Starts watching.</summary>
    /// <param name="workingDir">The working directory of the repository.</param>
    /// <param name="gitDirs">The git directory and, for a linked worktree, the common git directory (refs live there).</param>
    /// <param name="changed">Called on a pool thread once a burst of changes settled.</param>
    public RepositoryWatcher(string workingDir, IEnumerable<string> gitDirs, Action<RepositoryChangeBatch> changed, TimeSpan? debounce = null, TimeSpan? maxDelay = null)
    {
        _workingDir = Normalize(workingDir);
        _gitDirs = [.. gitDirs.Where(dir => !string.IsNullOrEmpty(dir)).Select(Normalize).Distinct(StringComparer.FromComparison(PathComparison))];
        _changed = changed;
        _debounce = debounce ?? DefaultDebounce;
        _maxDelay = maxDelay ?? DefaultMaxDelay;
        _timer = new Timer(_ => Flush());

        if (IsUnreliableLocation(workingDir))
        {
            Problem = "Changes on network drives and WSL paths are not reported reliably; refreshing on focus and every few seconds instead.";
        }

        try
        {
            Watch(_workingDir);
            foreach (string gitDir in _gitDirs.Where(dir => !IsUnder(dir, _workingDir)))
            {
                Watch(gitDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            DisposeWatchers();
            Problem = DescribeStartFailure(ex);
        }
    }

    /// <summary>Raised on the UI thread when "Automatically refresh on file changes" was switched on or off.</summary>
    public static event Action? SettingChanged;

    /// <summary>Tells the open windows that "Automatically refresh on file changes" changed.</summary>
    public static void NotifySettingChanged() => SettingChanged?.Invoke();

    /// <summary>True when events can be trusted; false means: refresh on focus and on a timer as well.</summary>
    public bool IsReliable => Problem is null;

    /// <summary>Why the watcher is not reliable, in words for the user; <see langword="null"/> when it is.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    ///  Sorts one changed path into the kind of change it is, or <see cref="RepositoryChanges.None"/> when it does not matter.
    /// </summary>
    /// <param name="fullPath">The absolute path of the changed file or directory.</param>
    /// <param name="workingDir">The working directory.</param>
    /// <param name="gitDirs">The git directory and the common git directory.</param>
    /// <param name="workTreePath">The path relative to the working directory, when it is a working tree change.</param>
    public static RepositoryChanges Classify(string fullPath, string workingDir, IReadOnlyList<string> gitDirs, out string? workTreePath)
    {
        workTreePath = null;
        string path = Normalize(fullPath);

        foreach (string gitDir in gitDirs)
        {
            string normalizedGitDir = Normalize(gitDir);
            if (string.Equals(path, normalizedGitDir, PathComparison))
            {
                return RepositoryChanges.None;
            }

            if (IsUnder(path, normalizedGitDir))
            {
                return ClassifyGitDirEntry(path[(normalizedGitDir.Length + 1)..]);
            }
        }

        string normalizedWorkingDir = Normalize(workingDir);
        if (!IsUnder(path, normalizedWorkingDir))
        {
            return RepositoryChanges.None;
        }

        string relative = path[(normalizedWorkingDir.Length + 1)..];

        // A nested repository or a submodule's own git directory ("sub/.git/..."): its internals are not our changes.
        if (relative.Split('/').Any(segment => string.Equals(segment, ".git", PathComparison)))
        {
            return RepositoryChanges.None;
        }

        workTreePath = relative;
        return RepositoryChanges.WorkTree;
    }

    private static RepositoryChanges ClassifyGitDirEntry(string relative)
    {
        // index.lock, HEAD.lock, refs/heads/main.lock...: git's own temporary files, renamed over the real file when done.
        if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || IgnoredGitDirectories.Any(dir => relative.StartsWith(dir, StringComparison.Ordinal) || relative == dir.TrimEnd('/')))
        {
            return RepositoryChanges.None;
        }

        if (relative == "index" || relative.StartsWith("info/", StringComparison.Ordinal))
        {
            // Staging, or info/exclude which changes the untracked files.
            return RepositoryChanges.WorkTree;
        }

        if (relative == "packed-refs" || relative == "refs" || relative.StartsWith("refs/", StringComparison.Ordinal) || relative == "config")
        {
            return RepositoryChanges.Refs;
        }

        // A checkout, merge, rebase or cherry-pick changes HEAD and the working tree together.
        if (StateFiles.Contains(relative, StringComparer.Ordinal)
            || StateDirectories.Any(dir => relative.StartsWith(dir, StringComparison.Ordinal) || relative == dir.TrimEnd('/')))
        {
            return RepositoryChanges.All;
        }

        // FETCH_HEAD, COMMIT_EDITMSG, description, gitk.cache...: nothing the UI shows.
        return RepositoryChanges.None;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _timer.Dispose();
        DisposeWatchers();
    }

    private void Watch(string directory)
    {
        FileSystemWatcher watcher = new(directory)
        {
            IncludeSubdirectories = true,

            // No LastAccess, Attributes or Security: antivirus scanners and indexers touch those all the time.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        if (OperatingSystem.IsWindows())
        {
            // The maximum; a checkout of thousands of files overflows a smaller buffer at once.
            watcher.InternalBufferSize = 64 * 1024;
        }

        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Deleted += OnEvent;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        _watchers.Add(watcher);
        watcher.EnableRaisingEvents = true;
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => Add(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both names matter: "index.lock" renamed to "index" is a change of the index.
        Add(e.OldFullPath);
        Add(e.FullPath);
    }

    private void OnError(object? sender, ErrorEventArgs e)
    {
        // Buffer overflow (Windows) or a lost inotify queue (Linux): events were dropped, so refresh everything.
        Exception exception = e.GetException();
        if (exception is not InternalBufferOverflowException)
        {
            Problem = DescribeStartFailure(exception);
        }

        lock (_lock)
        {
            _pending |= RepositoryChanges.All;
            _pendingPaths = null;
            ScheduleLocked();
        }
    }

    private void Add(string fullPath)
    {
        RepositoryChanges changes = Classify(fullPath, _workingDir, _gitDirs, out string? workTreePath);
        if (changes == RepositoryChanges.None)
        {
            return;
        }

        lock (_lock)
        {
            _pending |= changes;
            if (workTreePath is not null && _pendingPaths is not null)
            {
                _pendingPaths.Add(workTreePath);
                if (_pendingPaths.Count > MaxReportedPaths)
                {
                    _pendingPaths = null;
                }
            }

            ScheduleLocked();
        }
    }

    // Trailing debounce, but a burst that keeps going is still reported every _maxDelay.
    private void ScheduleLocked()
    {
        if (_disposed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_burstStart == default)
        {
            _burstStart = now;
        }

        TimeSpan untilMax = _maxDelay - (now - _burstStart);
        TimeSpan due = untilMax < _debounce ? (untilMax < TimeSpan.Zero ? TimeSpan.Zero : untilMax) : _debounce;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        RepositoryChangeBatch batch;
        lock (_lock)
        {
            if (_disposed || _pending == RepositoryChanges.None)
            {
                return;
            }

            batch = new RepositoryChangeBatch(_pending, _pendingPaths is null ? null : [.. _pendingPaths]);
            _pending = RepositoryChanges.None;
            _pendingPaths = NewPathSet();
            _burstStart = default;
        }

        try
        {
            _changed(batch);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Repository change handler failed: {ex}");
        }
    }

    private void DisposeWatchers()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private static HashSet<string> NewPathSet() => new(StringComparer.FromComparison(PathComparison));

    private static string DescribeStartFailure(Exception ex)
        => OperatingSystem.IsLinux() && ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase)
            ? "The inotify watch limit is reached, so changes are not detected automatically. "
              + "Raise fs.inotify.max_user_watches (e.g. sudo sysctl fs.inotify.max_user_watches=524288); refreshing on focus meanwhile."
            : $"Changes are not detected automatically ({ex.Message}); refreshing on focus and every few seconds instead.";

    /// <summary>True for network drives and WSL paths, where file system events are missing or late.</summary>
    public static bool IsUnreliableLocation(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // UNC paths: \\server\share, \\wsl$\Ubuntu, \\wsl.localhost\Ubuntu.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private static bool IsUnder(string path, string directory)
        => path.Length > directory.Length
           && path[directory.Length] == '/'
           && path.StartsWith(directory, PathComparison);
}
