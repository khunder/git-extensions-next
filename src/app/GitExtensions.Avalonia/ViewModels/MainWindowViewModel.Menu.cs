using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>What the toolbar's Pull button does when it is clicked itself (not one of the drop-down entries).</summary>
public enum PullAction
{
    Merge,
    Rebase,
    Fetch,
    FetchAll,
    FetchAndPruneAll,
}

// The main menu and the main toolbar: window title, commit counter and the commands that only they use.
public sealed partial class MainWindowViewModel
{
    public const string NotAvailableText = "Not available yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultPullMerge), nameof(IsDefaultPullRebase), nameof(IsDefaultPullFetch), nameof(IsDefaultPullFetchAll), nameof(IsDefaultPullFetchAndPrune), nameof(PullToolTip))]
    private PullAction _defaultPullAction;

    /// <summary>
    ///  "&lt;repository&gt; (&lt;branch&gt;) - Git Extensions Next", or just "Git Extensions" on the dashboard.
    /// </summary>
    public string Title
    {
        get
        {
            if (!HasOpenedRepository)
            {
                return "Git Extensions Next";
            }

            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(RepositoryPath));
            if (name.Length == 0)
            {
                name = RepositoryPath;
            }

            return string.IsNullOrEmpty(Branch) ? $"{name} - Git Extensions Next" : $"{name} ({Branch}) - Git Extensions Next";
        }
    }

    /// <summary>The toolbar's Commit button: "Commit (3)" with the number of changed files.</summary>
    public string CommitButtonText => ChangedFiles.Count == 0 ? "Commit" : $"Commit ({ChangedFiles.Count})";

    /// <summary>The toolbar Commit icon: the green tick when the working tree is clean, the orange-red one when there are changes (like the original).</summary>
    public string CommitButtonIcon => ChangedFiles.Count == 0 ? "RepoStateClean" : "RepoStateDirty";

    public bool IsDefaultPullMerge => DefaultPullAction == PullAction.Merge;

    public bool IsDefaultPullRebase => DefaultPullAction == PullAction.Rebase;

    public bool IsDefaultPullFetch => DefaultPullAction == PullAction.Fetch;

    public bool IsDefaultPullFetchAll => DefaultPullAction == PullAction.FetchAll;

    public bool IsDefaultPullFetchAndPrune => DefaultPullAction == PullAction.FetchAndPruneAll;

    public string PullToolTip => DefaultPullAction switch
    {
        PullAction.Rebase => "Pull (rebase)",
        PullAction.Fetch => "Fetch",
        PullAction.FetchAll => "Fetch all",
        PullAction.FetchAndPruneAll => "Fetch and prune all",
        _ => "Pull (merge)",
    };

    /// <summary>The local branches for the toolbar's branch list.</summary>
    public ObservableCollection<string> LocalBranches => Refs.LocalBranchNames;

    /// <summary>
    ///  Whether the selected node of the branches tree can be merged into / rebased onto the current branch.
    /// </summary>
    public bool CanMergeSelectedRef => HasOpenedRepository && Refs.SelectedNode is { CanMergeOrRebase: true };

    public bool IsSelectedRefTag => HasOpenedRepository && Refs.SelectedNode is { Kind: RefNodeKind.Tag };

    private void InitializeMenu()
    {
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(HasOpenedRepository):
                case nameof(Branch):
                case nameof(RepositoryPath):
                    OnPropertyChanged(nameof(Title));
                    OnPropertyChanged(nameof(CanMergeSelectedRef));
                    OnPropertyChanged(nameof(IsSelectedRefTag));
                    RefreshCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(IsBusy):
                    RefreshCommand.NotifyCanExecuteChanged();
                    break;
            }
        };
        ChangedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CommitButtonText));
            OnPropertyChanged(nameof(CommitButtonIcon));
        };
        Refs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RefsTreeViewModel.SelectedNode))
            {
                OnPropertyChanged(nameof(CanMergeSelectedRef));
                OnPropertyChanged(nameof(IsSelectedRefTag));
            }
        };
    }

    // ---- Repository ----

    private bool CanRefresh() => HasOpenedRepository && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => OpenAsync(RepositoryPath);

    /// <summary>The dashboard's Refresh: reloads the list of recent repositories.</summary>
    [RelayCommand]
    private Task RefreshDashboardAsync() => Recent.LoadAsync();

    [RelayCommand]
    private void OpenFileExplorer()
    {
        if (_module is { } module)
        {
            ShellLauncher.Open(module.WorkingDir);
        }
    }

    [RelayCommand]
    private void OpenShell()
    {
        if (_module is { } module && ShellLauncher.OpenTerminal(module.WorkingDir) is { } error)
        {
            ErrorMessage = error;
        }
    }

    [RelayCommand]
    private void ShowReflog() => ShowTools(RepositoryToolsViewModel.ReflogTab);

    [RelayCommand]
    private void ShowSubmodules() => ShowTools(RepositoryToolsViewModel.SubmodulesTab);

    [RelayCommand]
    private void ShowWorktrees() => ShowTools(RepositoryToolsViewModel.WorktreesTab);

    private void ShowTools(int tab)
    {
        if (!HasOpenedRepository)
        {
            return;
        }

        Tools.Open(tab);
    }

    [RelayCommand]
    private Task UpdateAllSubmodulesAsync() => HasOpenedRepository ? Remote.RunGitAsync("Update all submodules", ["submodule", "update", "--init", "--recursive"]) : Task.CompletedTask;

    [RelayCommand]
    private Task SynchronizeAllSubmodulesAsync() => HasOpenedRepository ? Remote.RunGitAsync("Synchronize all submodules", ["submodule", "sync", "--recursive"]) : Task.CompletedTask;

    [RelayCommand]
    private Task CompressDatabaseAsync() => HasOpenedRepository ? Remote.RunGitAsync("Compress git database", ["gc"]) : Task.CompletedTask;

    /// <summary>
    ///  Opens a file of the repository ("<c>.gitignore</c>", "<c>.mailmap</c>") with the default editor, creating it when missing.
    /// </summary>
    [RelayCommand]
    private void EditWorkingTreeFile(string? name) => EditFile(name, gitDirectory: false);

    /// <summary>
    ///  Opens a file inside the git directory ("<c>info/exclude</c>", "<c>config</c>") with the default editor.
    /// </summary>
    [RelayCommand]
    private void EditGitDirectoryFile(string? name) => EditFile(name, gitDirectory: true);

    private void EditFile(string? name, bool gitDirectory)
    {
        if (_module is not { } module || string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            string path = Path.GetFullPath(Path.Combine(gitDirectory ? module.WorkingDirGitDir : module.WorkingDir, name));
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            ShellLauncher.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open {name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteIndexLock()
    {
        if (_module is not { } module)
        {
            return;
        }

        string path = Path.Combine(module.WorkingDirGitDir, "index.lock");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Status = "Deleted index.lock.";
            }
            else
            {
                Status = "There is no index.lock.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not delete index.lock: {ex.Message}";
        }
    }

    // ---- Navigate ----

    [RelayCommand]
    private void GoToCurrentRevision()
    {
        // The history starts at HEAD.
        if (Commits.Count > 0)
        {
            SelectedCommit = Commits[0];
        }
    }

    [RelayCommand]
    private void GoToParentCommit()
    {
        if (SelectedCommit?.Revision is { HasParent: true } revision)
        {
            SelectCommit(revision.FirstParentId);
        }
    }

    [RelayCommand]
    private void GoToChildCommit()
    {
        if (SelectedCommit is { } selected)
        {
            CommitItemViewModel? child = Commits.FirstOrDefault(c => c.Revision.ParentIds?.Contains(selected.ObjectId) == true);
            if (child is not null)
            {
                SelectedCommit = child;
            }
        }
    }

    [RelayCommand]
    private void NextChange() => SelectNextCommit();

    [RelayCommand]
    private void PreviousChange() => SelectPreviousCommit();

    // ---- View ----

    [RelayCommand]
    private void ToggleLeftPanel() => UiRequested?.Invoke(UiTarget.ToggleSidebar);

    [RelayCommand]
    private static void SetTheme(ThemeChoice choice) => ThemeService.Current.Set(choice);

    // ---- Commands ----

    /// <summary>
    ///  Asks the user a yes/no question (title, message, confirm button text); set by the window. Without it (tests,
    ///  headless) actions run without asking.
    /// </summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    ///  Undoes the newest commit of the branch like GitHub Desktop: <c>git reset --soft HEAD~1</c>, so the commit goes away
    ///  and its changes come back staged, ready to commit again. Asks first (unless switched off in the settings) and warns
    ///  when the commit is already on a remote, where undoing it means the next push has to force-push.
    /// </summary>
    [RelayCommand]
    private async Task UndoLastCommitAsync()
    {
        if (!HasOpenedRepository || _module is not { } module)
        {
            return;
        }

        (bool hasParent, bool pushed, string subject) = await Task.Run(() =>
        (
            module.GitExecutable.Execute("rev-parse --verify --quiet HEAD~1", throwOnErrorExit: false).ExitedSuccessfully,
            module.GitExecutable.Execute("branch --remotes --contains HEAD", throwOnErrorExit: false).StandardOutput.Trim().Length > 0,
            module.GitExecutable.Execute("log -1 --format=%s", throwOnErrorExit: false).StandardOutput.Trim()));

        if (!hasParent)
        {
            ErrorMessage = "This is the first commit of the repository: there is no earlier commit to go back to.";
            return;
        }

        if (!AppSettings.DontConfirmUndoLastCommit.Value && Confirm is { } confirm)
        {
            string message = $"Undo \"{subject}\"? The commit is removed and its changes go back to the Changes tab, ready to commit again.";
            if (pushed)
            {
                message += "\n\nThis commit is already on the remote. Undoing it here means your next push has to force-push, and others may already have it.";
            }

            if (!await confirm("Undo commit", message, "Undo commit"))
            {
                return;
            }
        }

        await Remote.RunGitAsync("Undo last commit", ["reset", "--soft", "HEAD~1"]);
    }

    [RelayCommand]
    private void ManageStashes()
    {
        if (HasOpenedRepository)
        {
            Stash.Open();
        }
    }

    [RelayCommand]
    private void CreateBranch()
    {
        UiRequested?.Invoke(UiTarget.ShowSidebar);
        Refs.RequestNewBranchCommand.Execute(null);
    }

    [RelayCommand]
    private void CreateTag()
    {
        UiRequested?.Invoke(UiTarget.ShowSidebar);
        Refs.RequestNewTagCommand.Execute(null);
    }

    [RelayCommand]
    private void DeleteSelectedTag()
    {
        if (Refs.SelectedNode is { Kind: RefNodeKind.Tag } node)
        {
            UiRequested?.Invoke(UiTarget.ShowSidebar);
            Refs.RequestDeleteTagCommand.Execute(node);
        }
    }

    [RelayCommand]
    private Task MergeSelectedRefAsync() => OpenMergeDialogAsync(CanMergeSelectedRef ? Refs.SelectedNode!.FullName : null);

    [RelayCommand]
    private Task RebaseOntoSelectedRefAsync() => OpenRebaseDialogAsync(CanMergeSelectedRef ? Refs.SelectedNode!.FullName : null);

    [RelayCommand]
    private Task ShowCheckoutBranchDialogAsync() => OpenCheckoutDialogAsync(Refs.SelectedNode is { Kind: RefNodeKind.LocalBranch or RefNodeKind.RemoteBranch, IsCurrent: false } node ? node.FullName : null);

    /// <summary>Checks out the branch picked in the toolbar's branch list.</summary>
    public Task CheckoutBranchAsync(string name) => name == Branch ? Task.CompletedTask : Refs.CheckoutRefAsync(name);

    // ---- Pull / Push ----

    [RelayCommand]
    private Task RunDefaultPullAsync() => DefaultPullAction switch
    {
        PullAction.Rebase => PullRebaseAsync(),
        PullAction.Fetch => FetchOnlyAsync(),
        PullAction.FetchAll => FetchAllAsync(),
        PullAction.FetchAndPruneAll => FetchAndPruneAllAsync(),
        _ => PullMergeAsync(),
    };

    [RelayCommand]
    private Task PullMergeAsync()
    {
        Remote.PullCommand.Execute(null);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task PullRebaseAsync()
    {
        RemoteOperationsViewModel remote = Remote;
        if (!remote.HasRepository || remote.IsRunning)
        {
            return Task.CompletedTask;
        }

        List<string> args = ["pull", "--progress", "--rebase"];
        if (!remote.HasUpstream && remote.SelectedRemote is { } name && remote.CurrentBranch is { } branch)
        {
            args.Add(name);
            args.Add(branch);
        }

        return remote.RunGitAsync("Pull (rebase)", args);
    }

    [RelayCommand]
    private Task FetchOnlyAsync()
    {
        Remote.FetchCommand.Execute(null);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task FetchAllAsync() => Remote.HasRepository && !Remote.IsRunning ? Remote.RunGitAsync("Fetch all", ["fetch", "--progress", "--all"]) : Task.CompletedTask;

    [RelayCommand]
    private Task FetchAndPruneAllAsync() => Remote.HasRepository && !Remote.IsRunning ? Remote.RunGitAsync("Fetch and prune all", ["fetch", "--progress", "--all", "--prune"]) : Task.CompletedTask;

    [RelayCommand]
    private void SetDefaultPullAction(PullAction action) => DefaultPullAction = action;

    // ---- Help ----

    [RelayCommand]
    private static void OpenDocumentation() => ShellLauncher.OpenUrl("https://git-extensions-documentation.readthedocs.org");

    [RelayCommand]
    private static void ReportIssue() => ShellLauncher.OpenUrl("https://github.com/gitextensions/gitextensions/issues");

    [RelayCommand]
    private static void OpenChangelog() => ShellLauncher.OpenUrl("https://github.com/gitextensions/gitextensions/blob/master/src/app/GitUI/Resources/ChangeLog.md");

    [RelayCommand]
    private void ShowShortcuts() => ToggleHelp();

    /// <summary>Raised when "About Git Extensions" is chosen; the view shows the dialog.</summary>
    public event Action? AboutRequested;

    [RelayCommand]
    private void ShowAbout() => AboutRequested?.Invoke();
}
