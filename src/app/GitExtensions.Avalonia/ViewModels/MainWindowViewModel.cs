using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitCommands.UserRepositoryHistory;
using GitExtensions.Desktop.History;
using GitExtensions.Desktop.Services;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    // Captured on the creating (UI) thread: callers run on pool threads, and touching the static Dispatcher.UIThread there
    // can create a bogus dispatcher while a headless test session is being set up.
    private readonly Avalonia.Threading.Dispatcher _dispatcher = Avalonia.Threading.Dispatcher.UIThread;

    private readonly GitEnvironment _git;
    private readonly IFolderPicker _folderPicker;
    private CancellationTokenSource? _loadCancellation;
    private GitModule? _module;

    [ObservableProperty]
    private string _repositoryPath = string.Empty;

    [ObservableProperty]
    private string _status = "Enter or browse to a Git repository.";

    [ObservableProperty]
    private string? _branch;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private FileDiffViewModel? _diff;

    [ObservableProperty]
    private ChangedFileViewModel? _selectedChangedFile;

    [ObservableProperty]
    private CommitItemViewModel? _selectedCommit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartView))]
    private bool _hasOpenedRepository;

    /// <param name="history">The repository history behind the dashboard and the recent list; the user's own one by default.</param>
    public MainWindowViewModel(GitEnvironment git, IFolderPicker folderPicker, ILocalRepositoryManager? history = null)
    {
        _git = git;
        _folderPicker = folderPicker;
        _history = history ?? RepositoryHistoryManager.Locals;
        Recent = new RecentRepositoriesViewModel(_history, OpenAsync);
        git.Messages.MessageReported += message => ErrorMessage = message;
        Refs = new RefsTreeViewModel(message => Status = message, SelectCommit, () => OpenAsync(RepositoryPath));
        InitializeHistoryExtras();
        InitializeGrid();
        InitializeViewOptions();
        InitializeOperations();
        InitializeCloneInit();
        InitializeBranchManagement();
        InitializeMenu();
        InitializeDashboard();
        InitializeNavigation();
        InitializeChangesTab();
        InitializeAutoRefresh();
        InitializeSimpleMode();
        InitializeUnpushed();
    }

    public RecentRepositoriesViewModel Recent { get; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    ///  The dashboard (start page) is shown until a repository has been opened, and again after it was closed.
    /// </summary>
    public bool ShowStartView => !HasOpenedRepository;

    /// <summary>
    ///  Shows an exception nobody handled in the error banner instead of letting it end the application.
    /// </summary>
    public void ReportUnexpectedError(Exception exception)
        => ErrorMessage = $"Unexpected error: {exception.Message}";

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    public RefsTreeViewModel Refs { get; }

    /// <summary>
    ///  Selects the commit in the history list if it is loaded.
    /// </summary>
    public void SelectCommit(ObjectId id)
        => SelectedCommit = Commits.FirstOrDefault(commit => commit.ObjectId == id) ?? SelectedCommit;

    public RangeObservableCollection<CommitItemViewModel> Commits { get; } = [];

    public CommitDetailsViewModel CommitDetails { get; } = new();

    public RangeObservableCollection<ChangedFileViewModel> ChangedFiles { get; } = [];

    /// <summary>
    ///  Opens the repository containing <paramref name="path"/> and loads its branch, history and changes.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        if (_loadCancellation is not null)
        {
            await _loadCancellation.CancelAsync();
        }

        CancelHistoryReload();
        StopWatching();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;

        RepositoryPath = path;
        _module = null;
        SelectedCommit = null;
        Commits.Clear();
        GraphLaneCount = 1;
        ChangedFiles.Clear();
        Diff = null;
        Branch = null;
        Refs.Clear();
        IsBusy = true;
        Status = $"Opening {path}…";

        try
        {
            GitModule? module = await Task.Run(() => _git.OpenRepository(path), cancellationToken);

            // A Task.Run that already started is not interrupted by the token: never let an outdated open touch the state.
            cancellationToken.ThrowIfCancellationRequested();
            if (module is null)
            {
                Status = $"{path} is not inside a Git repository.";
                ResetChangesTab();
                return;
            }

            RepositoryPath = module.WorkingDir;
            _module = module;
            StartWatching(module);
            Diff = new FileDiffViewModel(module, RefreshChangesAsync, message => ErrorMessage = message);
            Commit = CreateCommitViewModel(module);
            HasOpenedRepository = true;
            ResetChangesTab(module.WorkingDir);
            EnsureChangesTab();
            _ = RecordRecentAsync(module.WorkingDir);

            // The git calls block, so keep them off the UI thread.
            (string branch, (List<ChangedFileViewModel> changes, string? statusError), Dictionary<ObjectId, CommitRefs> refs, string user) = await Task.Run(
                () => (
                    module.GetSelectedBranch(),
                    ReadChanges(module),
                    GetRefsByCommit(module),
                    module.GetEffectiveSetting("user.name")),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Branch = branch;
            PrepareGrid(module.WorkingDir, refs, user);
            if (statusError is not null)
            {
                ErrorMessage = statusError;
            }

            await Refs.LoadAsync(module);
            cancellationToken.ThrowIfCancellationRequested();
            ChangedFiles.AddRange(changes);

            Status = "Loading history…";
            RevisionFilter openFilter = CurrentFilter;
            int generation = Interlocked.Increment(ref _historyGeneration);
            await Task.Run(() => LoadHistory(module, refs, openFilter, user, generation, cancellationToken), cancellationToken);

            // Let the batches still queued for the UI thread land before reporting the final count.
            await _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);

            Status = $"{Commits.Count} commits shown · {ChangedFiles.Count} changed files";
        }
        catch (OperationCanceledException)
        {
            // A newer open request replaced this one.
        }
        catch (Exception ex)
        {
            Status = $"Failed to open {path}: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    ///  Reloads the list of changed files (e.g. after staging) and keeps the selection when possible.
    /// </summary>
    public Task RefreshChangesAsync() => RefreshChangesAsync(changedPaths: null);

    /// <param name="changedPaths">
    ///  The working tree paths known to have changed, or <see langword="null"/> when unknown. When the list of changed files
    ///  is the same as before, only the diff of the selected file is shown again, and only when that file is among them:
    ///  an automatic refresh then leaves the lists, the selection and the scroll position alone.
    /// </param>
    private async Task RefreshChangesAsync(IReadOnlyCollection<string>? changedPaths)
    {
        GitModule? module = _module;
        if (module is null)
        {
            return;
        }

        if (changedPaths is not null)
        {
            List<ChangedFileViewModel> current;
            try
            {
                string? statusError;
                (current, statusError) = await Task.Run(() => ReadChanges(module));
                if (statusError is not null)
                {
                    ErrorMessage = statusError;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not read the changes of {module.WorkingDir}: {ex.Message}";
                return;
            }

            if (!ReferenceEquals(_module, module))
            {
                return;
            }

            if (HasSameChanges(ChangedFiles, current))
            {
                if (SelectedChangedFile is { } selected && Diff is { } shownDiff && changedPaths.Contains(selected.Name))
                {
                    await shownDiff.ShowAsync(selected);
                }

                if (changedPaths.Count > 0 && IsChangesTabSelected && ChangesTab is { } tab)
                {
                    await tab.ShowStatusAsync(current);
                }

                return;
            }

            await ShowChangesAsync(current);
            return;
        }

        List<ChangedFileViewModel> changes;
        try
        {
            string? statusError;
            (changes, statusError) = await Task.Run(() => ReadChanges(module));
            if (statusError is not null)
            {
                ErrorMessage = statusError;
            }
        }
        catch (Exception ex)
        {
            // E.g. the repository folder vanished: keep the app alive and say so.
            ErrorMessage = $"Could not read the changes of {module.WorkingDir}: {ex.Message}";
            return;
        }

        await ShowChangesAsync(changes);
    }

    // Two status lists show the same thing when every entry has the same path, kind and stage.
    private static bool HasSameChanges(IReadOnlyList<ChangedFileViewModel> shown, IReadOnlyList<ChangedFileViewModel> read)
        => shown.Count == read.Count
           && shown.Zip(read).All(pair =>
               pair.First.Name == pair.Second.Name
               && pair.First.Status == pair.Second.Status
               && pair.First.IsStaged == pair.Second.IsStaged
               && pair.First.Item.OldName == pair.Second.Item.OldName
               && pair.First.Item.IsTracked == pair.Second.Item.IsTracked
               && pair.First.Item.IsSubmodule == pair.Second.Item.IsSubmodule);

    private async Task ShowChangesAsync(List<ChangedFileViewModel> changes)
    {
        ChangedFileViewModel? previous = SelectedChangedFile;
        ChangedFiles.ReplaceAll(changes);

        // Prefer the same entry; a file that just moved between index and work tree keeps its name.
        SelectedChangedFile = previous is null
            ? null
            : changes.FirstOrDefault(c => c.Name == previous.Name && c.IsStaged == previous.IsStaged)
                ?? changes.FirstOrDefault(c => c.Name == previous.Name);
        if (SelectedChangedFile is null && Diff is { } diff)
        {
            await diff.ShowAsync(null);
        }

        if (IsChangesTabSelected && ChangesTab is { } changesTab)
        {
            // Reuse the status just read: the tab does not need to run git status (and more) again.
            await changesTab.ShowStatusAsync(changes);
        }
    }

    partial void OnSelectedChangedFileChanged(ChangedFileViewModel? value)
    {
        // Started from the UI thread on purpose (not ThreadHelper.FileAndForget, which hops to a background thread):
        // ShowAsync updates collections bound to the view. It does its own error handling and cancellation.
        if (Diff is { } diff)
        {
            _ = diff.ShowAsync(value);
        }
    }

    // The details load asynchronously and cancel each other; failures are reported by the details view-model itself.
    partial void OnSelectedCommitChanged(CommitItemViewModel? value)
    {
        if (value is not null)
        {
            IsWorkingTreeSelected = false;
        }

        _ = value is not null && _module is not null
            ? CommitDetails.LoadAsync(_module, value.ObjectId, value.Refs, GetChildren(value.ObjectId))
            : CommitDetails.ClearAsync();
    }

    private async Task RecordRecentAsync(string workingDir)
    {
        try
        {
            await Task.Run(() => _history.AddAsMostRecentAsync(workingDir));
            await Recent.LoadAsync();
            await Dashboard.LoadAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not update the recent repositories: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task OpenCurrentAsync()
        => string.IsNullOrWhiteSpace(RepositoryPath) ? Task.CompletedTask : OpenAsync(RepositoryPath.Trim());

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? folder = await _folderPicker.PickFolderAsync("Open Git repository");
        if (folder is not null)
        {
            await OpenAsync(folder);
        }
    }

    // git status failures arrive as a fake "Git Error" entry; show them as an error instead of as a file.
    private static (List<ChangedFileViewModel> Changes, string? Error) ReadChanges(GitModule module)
    {
        (List<GitItemStatus> items, string? error) = GitErrorMessages.SplitStatusError(module.GetAllChangedFilesWithoutLocks(), module.WorkingDir);
        return (items.Select(item => new ChangedFileViewModel(item)).ToList(), error);
    }

    private void LoadHistory(GitModule module, Dictionary<ObjectId, CommitRefs> refs, RevisionFilter filter, string? user, int generation, CancellationToken cancellationToken)
    {
        RevisionReader reader = new(module);

        // Stash entries are shown in the history like the original does ("Show stashes in history"): their commits are added to the walk and labelled stash@{n}.
        List<(string Id, string Name)> stashes = AppSettings.ShowStashes && filter.Branches == BranchFilterMode.AllBranches && !filter.HasText ? ReadStashCommits(module) : [];
        if (stashes.Count > 0)
        {
            refs = WithStashBadges(refs, stashes);
        }

        // A newer load (open or filter change, numbered on the UI thread) makes this one stale: its batches must not reach the list any more.
        int row = 0;

        // Commits that are not ancestors of the checked out branch are greyed. The log lists children before their parents,
        // so one pass collects the ancestors of HEAD. With a detached HEAD nothing is greyed.
        ObjectId? headId = refs.Where(pair => pair.Value.Badges?.Any(badge => badge.IsCurrent) == true).Select(pair => (ObjectId?)pair.Key).FirstOrDefault();
        HashSet<ObjectId> reachable = [];

        // The graph lanes and row view-models are computed here, on the reader's thread, so the UI thread only has to
        // insert the finished batch. The builder belongs to this load: a newer load never shares it.
        CommitGraphBuilder graphBuilder = new() { CollapseFromLane = Views.CommitGraphCell.MaxVisibleLanes - 1 };
        BatchObserver observer = new(batch =>
        {
            if (cancellationToken.IsCancellationRequested || generation != _historyGeneration)
            {
                return;
            }

            List<CommitItemViewModel> items = new(batch.Count);
            foreach (GitRevision revision in batch)
            {
                CommitItemViewModel item = CreateItem(revision, refs, filter, user, (row++ & 1) == 1, Actions);
                if (headId is { } head)
                {
                    bool onCurrentBranch = revision.ObjectId == head || reachable.Contains(revision.ObjectId);
                    if (onCurrentBranch)
                    {
                        foreach (ObjectId parent in revision.ParentIds ?? [])
                        {
                            reachable.Add(parent);
                        }
                    }

                    item.IsDimmed = !onCurrentBranch;
                }

                item.Graph = graphBuilder.Append(item.ObjectId.ToString(), revision.ParentIds?.Select(id => id.ToString()).ToList() ?? [], item.IsDimmed);
                items.Add(item);
            }

            int laneCount = Math.Max(1, graphBuilder.MaxLaneCount);
            _dispatcher.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested || generation != _historyGeneration)
                {
                    return;
                }

                Commits.AddRange(items);
                GraphLaneCount = laneCount;
                Status = $"Loading history… {Commits.Count} commits";
            });
        });

        reader.GetLog(observer, RevisionFilterArgs.Build(filter, EffectiveHistoryLimit, stashes.Select(s => s.Id)), pathFilter: RevisionFilterArgs.PathSpec(filter), hasNotes: false, autostashLabel: "autostash", cancellationToken);
    }

    private static List<(string Id, string Name)> ReadStashCommits(GitModule module)
    {
        try
        {
            ExecutionResult result = module.GitExecutable.Execute("stash list --format=%H%x1f%gd", throwOnErrorExit: false);
            return result.ExitedSuccessfully
                ? [.. result.StandardOutput.Split("\n", StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim('\r', '\n').Split('\u001f'))
                    .Where(parts => parts.Length == 2 && parts[0].Length >= 40)
                    .Select(parts => (parts[0], parts[1]))]
                : [];
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static Dictionary<ObjectId, CommitRefs> WithStashBadges(Dictionary<ObjectId, CommitRefs> refs, List<(string Id, string Name)> stashes)
    {
        Dictionary<ObjectId, CommitRefs> result = new(refs);
        foreach ((string id, string name) in stashes)
        {
            if (!ObjectId.TryParse(id, out ObjectId commit))
            {
                continue;
            }

            RefBadge badge = new(RefBadgeKind.Stash, name, false);
            result[commit] = result.TryGetValue(commit, out CommitRefs existing)
                ? new CommitRefs(existing.Joined, [.. existing.Badges, badge])
                : new CommitRefs(name, [badge]);
        }

        return result;
    }

    private sealed class BatchObserver(Action<IReadOnlyList<GitRevision>> onNext) : IObserver<IReadOnlyList<GitRevision>>
    {
        public void OnNext(IReadOnlyList<GitRevision> value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
