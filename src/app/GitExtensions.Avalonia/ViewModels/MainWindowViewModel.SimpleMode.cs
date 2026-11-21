using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Extensibility;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>What the one sync button of the simple mode does, like GitHub Desktop's "Fetch / Pull / Push origin".</summary>
public enum SyncAction
{
    /// <summary>The repository has no remote: nothing to sync with.</summary>
    None,

    /// <summary>The branch is not on the remote yet: push it and track it.</summary>
    Publish,

    /// <summary>The remote has commits this branch does not have (also when both sides have new commits).</summary>
    Pull,

    /// <summary>This branch has commits the remote does not have.</summary>
    Push,

    /// <summary>In sync as far as we know: look for new commits on the remote.</summary>
    Fetch,
}

/// <summary>A branch in the simple mode's branch drop-down; the current one is marked.</summary>
public sealed record BranchPickerItem(string Name, bool IsCurrent);

/// <summary>What choosing a branch in the simple mode's branch drop-down does.</summary>
public enum BranchPickerMode
{
    Switch,
    Merge,
    Rebase,
    Delete,
}

// The simple mode: a compact window with only the everyday actions, in plain words and without git jargon, like GitHub
// Desktop: switch or create a branch, update it from the main branch, merge another branch into it, and one button that
// fetches, pulls, pushes or publishes depending on the state. Nothing in it rewrites history or throws work away.
public sealed partial class MainWindowViewModel
{
    // A branch named like this is the main branch when the remote does not say which one it is.
    private static readonly string[] DefaultBranchCandidates = ["main", "master", "develop", "trunk"];

    /// <summary>The branch everything else is updated from (e.g. <c>main</c>); <see langword="null"/> when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateFromDefaultBranch), nameof(UpdateFromDefaultBranchText))]
    private string? _defaultBranch;

    // What "Update from main" merges: the remote's copy when there is one (it is what others pushed), else the local branch.
    private string? _defaultBranchMergeSource;

    [ObservableProperty]
    private string _branchFilter = string.Empty;

    /// <summary>What choosing a branch in the branch drop-down does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchPickerTitle), nameof(IsMergePicker), nameof(PickerCancelText))]
    private BranchPickerMode _pickerMode;

    /// <summary>The drop-down asks for a branch to act on (merge, rebase, delete) instead of one to switch to.</summary>
    public bool IsMergePicker
    {
        get => PickerMode != BranchPickerMode.Switch;
        set => PickerMode = value ? BranchPickerMode.Merge : BranchPickerMode.Switch;
    }

    public string PickerCancelText => PickerMode switch
    {
        BranchPickerMode.Rebase => "Cancel rebase",
        BranchPickerMode.Delete => "Cancel delete",
        _ => "Cancel merge",
    };

    /// <summary>Asks the toolbar to open the branch drop-down (the Branch menu entries that need a branch use it).</summary>
    public event Action? BranchPickerRequested;

    /// <summary>The window shows the simple mode (persisted with the view options).</summary>
    public bool IsSimpleMode => ViewOptions.SimpleMode;

    /// <summary>The window shows the full Git Extensions interface.</summary>
    public bool IsFullMode => !IsSimpleMode;

    /// <summary>The History tab shows the branches tree and the revision grid (full mode).</summary>
    public bool IsFullHistoryVisible => IsHistoryTabSelected && IsFullMode;

    /// <summary>The History tab shows the simple list of commits and the selected one (simple mode).</summary>
    public bool IsSimpleHistoryVisible => IsHistoryTabSelected && IsSimpleMode;

    /// <summary>The List | Graph switch of the simple history: Graph draws the branch lines in the same list.</summary>
    public bool IsHistoryGraphView
    {
        get => ViewOptions.SimpleHistoryGraph;
        set => ViewOptions.SimpleHistoryGraph = value;
    }

    public bool IsHistoryListView
    {
        get => !ViewOptions.SimpleHistoryGraph;
        set => ViewOptions.SimpleHistoryGraph = !value;
    }

    /// <summary>The List | Graph switch is shown: simple mode, History tab.</summary>
    public bool ShowHistoryViewSwitch => IsSimpleMode && IsHistoryTabSelected;

    /// <summary>
    ///  The simple history shows every file of the selected commit (a folder tree to browse) instead of the files it changed.
    ///  It is the File tree tab of the revision tabs, so the tree loads only while it is shown, and it stays chosen from
    ///  commit to commit.
    /// </summary>
    public bool IsBrowsingAllFiles
    {
        get => _revisionTabs?.SelectedTabIndex == RevisionTabsViewModel.FileTreeTab;
        set => RevisionTabs.SelectedTabIndex = value ? RevisionTabsViewModel.FileTreeTab : RevisionTabsViewModel.DiffTab;
    }

    public bool IsShowingChangedFiles
    {
        get => !IsBrowsingAllFiles;
        set => IsBrowsingAllFiles = !value;
    }

    /// <summary>The branches the branch picker lists, filtered by <see cref="BranchFilter"/>.</summary>
    public ObservableCollection<BranchPickerItem> PickerBranches { get; } = [];

    public string BranchPickerTitle => PickerMode switch
    {
        BranchPickerMode.Merge => $"Choose a branch to merge into {Branch}",
        BranchPickerMode.Rebase => $"Choose a branch to move the commits of {Branch} onto",
        BranchPickerMode.Delete => "Choose a branch to delete",
        _ => "Switch to a branch",
    };

    /// <summary>The name of the repository folder, for the "Current repository" button.</summary>
    public string RepositoryName => Path.GetFileName(Path.TrimEndingDirectorySeparator(RepositoryPath)) is { Length: > 0 } name ? name : RepositoryPath;

    public bool CanUpdateFromDefaultBranch => DefaultBranch is not null && Branch is not null && Branch != DefaultBranch;

    public string UpdateFromDefaultBranchText => $"Update from {DefaultBranch}";

    /// <summary>What the sync button does now.</summary>
    public SyncAction SyncAction
    {
        get
        {
            if (_remote is not { HasRepository: true } remote || remote.Remotes.Count == 0 || Branch is null)
            {
                return SyncAction.None;
            }

            if (!remote.HasUpstream)
            {
                return SyncAction.Publish;
            }

            return Refs.CurrentTrack switch
            {
                { Behind: > 0 } => SyncAction.Pull,
                { Ahead: > 0 } => SyncAction.Push,
                _ => SyncAction.Fetch,
            };
        }
    }

    private string RemoteName => _remote?.SelectedRemote ?? _remote?.Remotes.FirstOrDefault() ?? "origin";

    public string SyncTitle => SyncAction switch
    {
        SyncAction.None => "No remote",
        SyncAction.Publish => "Publish branch",
        SyncAction.Pull => $"Pull {RemoteName}",
        SyncAction.Push => $"Push {RemoteName}",
        _ => $"Fetch {RemoteName}",
    };

    public string SyncDetail => SyncAction switch
    {
        SyncAction.None => "This repository is only on this computer",
        SyncAction.Publish => $"Put this branch on {RemoteName}",
        SyncAction.Pull when Refs.CurrentTrack is { Ahead: > 0 } track => $"{track.Behind} to get, {track.Ahead} to send",
        SyncAction.Pull => $"{Refs.CurrentTrack?.Behind} new {Plural(Refs.CurrentTrack?.Behind, "commit")} to get",
        SyncAction.Push => $"{Refs.CurrentTrack?.Ahead} {Plural(Refs.CurrentTrack?.Ahead, "commit")} to send",
        _ => LastFetchedText,
    };

    /// <summary>When the remote was last fetched (from <c>FETCH_HEAD</c>), like GitHub Desktop's "Last fetched 2 minutes ago".</summary>
    public string LastFetchedText
    {
        get
        {
            DateTime? fetched = null;
            try
            {
                if (_module is { } module && Path.Combine(module.GitCommonDirectory, "FETCH_HEAD") is { } file && File.Exists(file))
                {
                    fetched = File.GetLastWriteTimeUtc(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            if (fetched is not { } time)
            {
                return "Never fetched";
            }

            TimeSpan age = DateTime.UtcNow - time;
            return age.TotalMinutes switch
            {
                < 1 => "Last fetched just now",
                < 2 => "Last fetched a minute ago",
                < 60 => $"Last fetched {(int)age.TotalMinutes} minutes ago",
                < 120 => "Last fetched an hour ago",
                < 1440 => $"Last fetched {(int)age.TotalHours} hours ago",
                _ => $"Last fetched {(int)age.TotalDays} {(age.TotalDays < 2 ? "day" : "days")} ago",
            };
        }
    }

    public bool CanSync => SyncAction != SyncAction.None && _remote is { IsRunning: false } && !IsBusy;

    private static string Plural(int? count, string word) => count == 1 ? word : word + "s";

    private void InitializeSimpleMode()
    {
        ViewOptions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewOptionsViewModel.SimpleHistoryGraph))
            {
                OnPropertyChanged(nameof(IsHistoryGraphView));
                OnPropertyChanged(nameof(IsHistoryListView));
                OnPropertyChanged(nameof(IsFullHistoryVisible));
                OnPropertyChanged(nameof(IsSimpleHistoryVisible));
            }

            if (e.PropertyName == nameof(ViewOptionsViewModel.SimpleMode))
            {
                OnPropertyChanged(nameof(IsSimpleMode));
                OnPropertyChanged(nameof(IsFullMode));
                OnChangesTabEnabledChanged();
                if (_remote is { } remote)
                {
                    remote.ShowOutputWhenRunning = !IsSimpleMode;
                    remote.IsOutputVisible &= !IsSimpleMode;
                }

                if (IsSimpleMode)
                {
                    SidebarTabIndex = ChangesTabIndex;
                }
            }
        };

        PropertyChanged += OnSimpleModePropertyChanged;
        Refs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RefsTreeViewModel.CurrentTrack))
            {
                NotifySyncChanged();
            }
        };
        Refs.LocalBranchNames.CollectionChanged += (_, _) => UpdatePickerBranches();
    }

    private void OnSimpleModePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsHistoryTabSelected) or nameof(SidebarTabIndex) or nameof(IsSimpleMode))
        {
            OnPropertyChanged(nameof(IsFullHistoryVisible));
            OnPropertyChanged(nameof(ShowHistoryViewSwitch));
            OnPropertyChanged(nameof(IsSimpleHistoryVisible));
        }

        switch (e.PropertyName)
        {
            case nameof(RepositoryPath):
                OnPropertyChanged(nameof(RepositoryName));
                break;
            case nameof(Branch):
                OnPropertyChanged(nameof(CanUpdateFromDefaultBranch));
                OnPropertyChanged(nameof(BranchPickerTitle));
                UpdatePickerBranches();
                NotifySyncChanged();

                // The sync button needs the remote state: create it (it then reloads itself on every branch change).
                if (_remote is null && Branch is not null)
                {
                    _ = Remote.ReloadStateAsync();
                }

                if (Branch is not null && _module is { } module)
                {
                    _ = FindDefaultBranchAsync(module);
                    OnPropertyChanged(nameof(HasGitHubRemote));
                }

                break;
            case nameof(IsBusy):
                OnPropertyChanged(nameof(CanSync));
                break;
        }
    }

    private void NotifySyncChanged()
    {
        OnPropertyChanged(nameof(SyncAction));
        OnPropertyChanged(nameof(SyncTitle));
        OnPropertyChanged(nameof(SyncDetail));
        OnPropertyChanged(nameof(LastFetchedText));
        OnPropertyChanged(nameof(CanSync));
    }

    partial void OnBranchFilterChanged(string value) => UpdatePickerBranches();

    partial void OnPickerModeChanged(BranchPickerMode value) => UpdatePickerBranches();

    private void UpdatePickerBranches()
    {
        string filter = BranchFilter.Trim();
        PickerBranches.Clear();
        foreach (string name in Refs.LocalBranchNames)
        {
            if ((filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                && !(IsMergePicker && name == Branch))
            {
                PickerBranches.Add(new BranchPickerItem(name, name == Branch));
            }
        }
    }

    [RelayCommand]
    private void ToggleSimpleMode() => ViewOptions.SimpleMode = !ViewOptions.SimpleMode;

    /// <summary>Switches to the branch, or merges it into the current one when the picker is in merge mode.</summary>
    [RelayCommand]
    private async Task PickBranchAsync(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        BranchPickerMode mode = PickerMode;
        PickerMode = BranchPickerMode.Switch;
        BranchFilter = string.Empty;
        switch (mode)
        {
            case BranchPickerMode.Merge:
                await Operation.MergeAsync(name);
                break;
            case BranchPickerMode.Rebase:
                await Operation.RebaseAsync(name);
                break;
            case BranchPickerMode.Delete:
                // git branch -d: a branch whose work is not merged anywhere is refused, so nothing is lost.
                await Refs.DeleteBranchCommand.ExecuteAsync(Refs.FindNode(RefNodeKind.LocalBranch, name));
                break;
            default:
                await CheckoutBranchAsync(name);
                break;
        }
    }

    [RelayCommand]
    private void StartMergePicker() => PickerMode = BranchPickerMode.Merge;

    /// <summary>Opens the branch drop-down to choose a branch for <paramref name="mode"/> (from the Branch menu).</summary>
    [RelayCommand]
    private void ChooseBranchFor(BranchPickerMode mode)
    {
        PickerMode = mode;
        BranchPickerRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelMergePicker() => PickerMode = BranchPickerMode.Switch;

    /// <summary>Merges the main branch (the remote's copy when there is one) into the current branch.</summary>
    [RelayCommand]
    private async Task UpdateFromDefaultBranchAsync()
    {
        if (_defaultBranchMergeSource is { } source && CanUpdateFromDefaultBranch)
        {
            await Operation.MergeAsync(source);
        }
    }

    /// <summary>The one sync button: publish, pull, push or fetch, whichever fits the state of the branch.</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        RemoteOperationsViewModel remote = Remote;
        switch (SyncAction)
        {
            case SyncAction.Publish or SyncAction.Push:
                await remote.PushCommand.ExecuteAsync(null);
                break;
            case SyncAction.Pull:
                await remote.PullCommand.ExecuteAsync(null);
                break;
            case SyncAction.Fetch:
                await remote.FetchCommand.ExecuteAsync(null);
                break;
        }
    }

    private async Task FindDefaultBranchAsync(GitModule module)
    {
        try
        {
            (string? name, string? source) = await Task.Run(() => FindDefaultBranch(module));
            if (ReferenceEquals(_module, module))
            {
                _defaultBranchMergeSource = source;
                DefaultBranch = name;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not find the main branch: {ex.Message}");
        }
    }

    // The remote's HEAD (origin/HEAD -> origin/main) says which branch is the main one; else a usual name that exists.
    private static (string? Name, string? MergeSource) FindDefaultBranch(GitModule module)
    {
        string remote = module.GitExecutable.Execute("remote", throwOnErrorExit: false).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(name => name == "origin" ? 0 : 1)
            .FirstOrDefault() ?? string.Empty;

        if (remote.Length > 0)
        {
            ExecutionResult head = module.GitExecutable.Execute($"symbolic-ref --quiet --short refs/remotes/{remote}/HEAD", throwOnErrorExit: false);
            string remoteHead = head.StandardOutput.Trim();
            if (head.ExitedSuccessfully && remoteHead.StartsWith(remote + "/", StringComparison.Ordinal))
            {
                return (remoteHead[(remote.Length + 1)..], remoteHead);
            }
        }

        foreach (string candidate in DefaultBranchCandidates)
        {
            if (RefExists(module, $"refs/heads/{candidate}"))
            {
                return (candidate, remote.Length > 0 && RefExists(module, $"refs/remotes/{remote}/{candidate}") ? $"{remote}/{candidate}" : candidate);
            }

            if (remote.Length > 0 && RefExists(module, $"refs/remotes/{remote}/{candidate}"))
            {
                return (candidate, $"{remote}/{candidate}");
            }
        }

        return (null, null);
    }

    private static bool RefExists(GitModule module, string fullName)
        => module.GitExecutable.Execute($"show-ref --verify --quiet {fullName}", throwOnErrorExit: false).ExitedSuccessfully;
}
