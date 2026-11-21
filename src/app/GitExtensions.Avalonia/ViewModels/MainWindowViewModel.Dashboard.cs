using GitCommands.UserRepositoryHistory;

namespace GitExtensions.Desktop.ViewModels;

// The dashboard (start page) and closing a repository (kept out of the main file).
public sealed partial class MainWindowViewModel
{
    private readonly ILocalRepositoryManager _history;

    /// <summary>The start page: recent and favorite repositories plus the create / open / clone links.</summary>
    public DashboardViewModel Dashboard { get; private set; } = null!;

    private void InitializeDashboard()
        => Dashboard = new DashboardViewModel(
            _history,
            OpenAsync,
            BrowseAsync,
            ShowInit,
            ShowClone,
            message => ErrorMessage = message);

    // Cancels the loads in flight and forgets everything of the repository, so nothing stale is shown on the
    // dashboard or by the next repository; the repository bar and the history list reset with it.
    partial void OnCloseRepository()
    {
        if (!HasOpenedRepository && _module is null)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation = null;
        _module = null;
        StopWatching();

        if (FileHistory.IsOpen)
        {
            FileHistory.CloseCommand.Execute(null);
        }

        SelectedCommit = null;
        Commits.Clear();
        GraphLaneCount = 1;
        SelectedChangedFile = null;
        ChangedFiles.Clear();
        Diff = null;
        Commit = null;
        ResetChangesTab();
        _commitModuleDir = null;
        Branch = null;
        Refs.Clear();
        _ = Operation.RefreshAsync();
        _stash?.Clear();
        _ = _tools?.OnRepositoryChangedAsync();
        _ = _remote?.ReloadStateAsync();
        RepositoryPath = string.Empty;
        IsBusy = false;
        ErrorMessage = null;
        Status = "No repository open.";
        HasOpenedRepository = false;
        _ = Dashboard.LoadAsync();
    }
}
