using System.ComponentModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCommands;
using GitExtensions.Desktop.Services;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.ViewModels;

// Refreshes the open repository by itself when files change outside the app or the window gains focus, like GitHub
// Desktop: no F5 needed. Only the affected parts are read again (status, or branches and history), selections are kept,
// and at most one refresh runs at a time; changes that arrive meanwhile are merged into one follow-up refresh.
public sealed partial class MainWindowViewModel
{
    /// <summary>How often the status is polled while the window is active and the file watcher cannot be trusted.</summary>
    public static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(5);

    private RepositoryWatcher? _watcher;
    private DispatcherTimer? _pollTimer;
    private bool _isWindowActive = true;

    // Single flight: what still has to be refreshed, and whether a refresh loop is running.
    private RepositoryChanges _pendingChanges;
    private HashSet<string>? _pendingPaths = [];
    private bool _autoRefreshRunning;

    // HEAD plus every ref: when it did not change, a "refs" event (e.g. a fetch that brought nothing) reloads nothing.
    private string? _refsFingerprint;

    /// <summary>Why changes are not detected automatically for the open repository, if they are not; shown as a hint.</summary>
    [ObservableProperty]
    private string? _autoRefreshProblem;

    private void InitializeAutoRefresh()
    {
        PropertyChanged += OnAutoRefreshPropertyChanged;
        RepositoryWatcher.SettingChanged += ApplyAutoRefreshSetting;
    }

    private void OnAutoRefreshPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Changes noticed while the app ran its own command (pull, checkout...) wait until it finished.
        if (e.PropertyName == nameof(IsBusy) && !IsBusy && _pendingChanges != RepositoryChanges.None)
        {
            StartAutoRefresh();
        }
    }

    /// <summary>Starts watching the repository that was just opened (replacing the previous watcher).</summary>
    private void StartWatching(GitModule module)
    {
        StopWatching();
        _refsFingerprint = null;
        _ = RememberRefsFingerprintAsync(module);

        if (!AppSettings.AutoRefreshOnFileChanges)
        {
            return;
        }

        _ = CreateWatcherAsync(module);
    }

    // Off the UI thread: reading the common git dir runs git, and inotify (Linux) adds one watch per folder of the tree.
    private async Task CreateWatcherAsync(GitModule module)
    {
        RepositoryWatcher watcher = await Task.Run(() => new RepositoryWatcher(
            module.WorkingDir,
            [module.WorkingDirGitDir, module.GitCommonDirectory],
            batch => _dispatcher.Post(() =>
            {
                if (ReferenceEquals(_module, module))
                {
                    RequestRefresh(batch.Changes, batch.WorkTreePaths);
                }
            })));

        // Another repository was opened, or the setting switched off, while the watcher started.
        if (!ReferenceEquals(_module, module) || !AppSettings.AutoRefreshOnFileChanges)
        {
            watcher.Dispose();
            return;
        }

        _watcher?.Dispose();
        _watcher = watcher;
        AutoRefreshProblem = watcher.Problem;
        UpdatePollTimer();
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        AutoRefreshProblem = null;
        _pendingChanges = RepositoryChanges.None;
        _pendingPaths = [];
        UpdatePollTimer();
    }

    /// <summary>Applies the "Automatically refresh on file changes" setting to the open repository.</summary>
    private void ApplyAutoRefreshSetting()
    {
        if (_module is { } module)
        {
            StartWatching(module);
        }
    }

    /// <summary>
    ///  Called when the main window gains or loses focus. Gaining it checks the status again (it catches whatever the
    ///  watcher missed, or everything when there is no watcher), like GitHub Desktop.
    /// </summary>
    public void OnWindowActiveChanged(bool isActive)
    {
        _isWindowActive = isActive;
        UpdatePollTimer();
        if (isActive && AppSettings.RefreshOnWindowActivated && _module is not null)
        {
            // Empty paths: only a different status list (or different refs) changes what is shown.
            RequestRefresh(RepositoryChanges.All, []);
        }
    }

    /// <summary>
    ///  Queues a refresh of <paramref name="changes"/>. A refresh already running picks it up when it is done.
    /// </summary>
    /// <param name="workTreePaths">The working tree paths that changed; <see langword="null"/> when unknown.</param>
    public void RequestRefresh(RepositoryChanges changes, IReadOnlyCollection<string>? workTreePaths)
    {
        if (_module is null || changes == RepositoryChanges.None)
        {
            return;
        }

        _pendingChanges |= changes;
        if (workTreePaths is null)
        {
            _pendingPaths = null;
        }
        else if (_pendingPaths is not null)
        {
            _pendingPaths.UnionWith(workTreePaths);
            if (_pendingPaths.Count > RepositoryWatcher.MaxReportedPaths)
            {
                _pendingPaths = null;
            }
        }

        StartAutoRefresh();
    }

    private void StartAutoRefresh()
    {
        if (!_autoRefreshRunning && !IsBusy)
        {
            _ = RunAutoRefreshAsync();
        }
    }

    private async Task RunAutoRefreshAsync()
    {
        _autoRefreshRunning = true;
        try
        {
            while (_pendingChanges != RepositoryChanges.None && !IsBusy && _module is { } module)
            {
                RepositoryChanges changes = _pendingChanges;
                HashSet<string>? paths = _pendingPaths;
                _pendingChanges = RepositoryChanges.None;
                _pendingPaths = [];

                try
                {
                    await ApplyChangesAsync(module, changes, paths);
                }
                catch (Exception ex)
                {
                    // A background refresh must never take the app down; the next change or F5 tries again.
                    System.Diagnostics.Trace.TraceWarning($"Automatic refresh failed: {ex}");
                }
            }
        }
        finally
        {
            _autoRefreshRunning = false;
        }
    }

    private async Task ApplyChangesAsync(GitModule module, RepositoryChanges changes, HashSet<string>? paths)
    {
        if (changes.HasFlag(RepositoryChanges.Refs))
        {
            (string fingerprint, string branch) = await Task.Run(() => (ReadRefsFingerprint(module), module.GetSelectedBranch()));
            if (!ReferenceEquals(_module, module))
            {
                return;
            }

            if (fingerprint != _refsFingerprint)
            {
                _refsFingerprint = fingerprint;
                await ReloadRefsAndHistoryAsync(module, branch);

                // A commit or checkout changes the status too, and the shown file contents.
                changes |= RepositoryChanges.WorkTree;
                paths = null;
            }
        }

        if (!changes.HasFlag(RepositoryChanges.WorkTree) || !ReferenceEquals(_module, module))
        {
            return;
        }

        // Builds and package managers write lots of ignored files (bin/, node_modules/): skip the status for those.
        if (paths is { Count: > 0 } && !changes.HasFlag(RepositoryChanges.Refs)
            && await Task.Run(() => AreAllIgnored(module, paths)))
        {
            return;
        }

        await RefreshChangesAsync(paths);
        if (IsWorkingTreeSelected && _revisionTabs is { } tabs && ReferenceEquals(_module, module))
        {
            await tabs.Diff.ShowWorkingTreeAsync(module);
        }
    }

    private async Task ReloadRefsAndHistoryAsync(GitModule module, string branch)
    {
        Dictionary<ObjectId, CommitRefs> refs = await Task.Run(() => GetRefsByCommit(module));
        if (!ReferenceEquals(_module, module))
        {
            return;
        }

        Branch = branch;
        PrepareGrid(module.WorkingDir, refs, _currentUser);
        await Refs.LoadAsync(module);
        await ReloadHistoryAsync();
        await Operation.RefreshAsync();
    }

    private async Task RememberRefsFingerprintAsync(GitModule module)
    {
        try
        {
            string fingerprint = await Task.Run(() => ReadRefsFingerprint(module));
            if (ReferenceEquals(_module, module) && _refsFingerprint is null)
            {
                _refsFingerprint = fingerprint;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not read the refs of {module.WorkingDir}: {ex.Message}");
        }
    }

    // One cheap git call: the symbolic HEAD plus every ref with its commit. Changes whenever a commit, checkout, fetch,
    // reset, tag or branch operation changed something the branches tree or the history shows.
    private static string ReadRefsFingerprint(GitModule module)
    {
        ExecutionResult head = module.GitExecutable.Execute("symbolic-ref -q HEAD", throwOnErrorExit: false);
        ExecutionResult refs = module.GitExecutable.Execute("show-ref --head", throwOnErrorExit: false);
        return head.StandardOutput + "\n" + refs.StandardOutput;
    }

    // git check-ignore lists the given paths that are ignored (tracked files never are).
    private static bool AreAllIgnored(GitModule module, IReadOnlyCollection<string> paths)
    {
        ExecutionResult result = module.GitExecutable.Execute(
            "check-ignore -z --stdin",
            writeInput: input =>
            {
                foreach (string path in paths)
                {
                    input.Write(path);
                    input.Write('\0');
                }
            },
            outputEncoding: Encoding.UTF8,
            throwOnErrorExit: false);

        // Exit code 1: none of the paths is ignored; anything else but 0 is an error, so refresh to be safe.
        if (result.ExitCode != 0)
        {
            return false;
        }

        HashSet<string> ignored = [.. result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        return paths.All(ignored.Contains);
    }

    // The fallback timer only runs while the window is active and the watcher cannot be trusted (or is switched off).
    private void UpdatePollTimer()
    {
        bool needed = _module is not null
            && _isWindowActive
            && AppSettings.AutoRefreshOnFileChanges
            && _watcher is { IsReliable: false };
        if (!needed)
        {
            _pollTimer?.Stop();
            return;
        }

        if (_pollTimer is null)
        {
            _pollTimer = new DispatcherTimer { Interval = FallbackPollInterval };
            _pollTimer.Tick += (_, _) => RequestRefresh(RepositoryChanges.All, null);
        }

        _pollTimer.Start();
    }
}
