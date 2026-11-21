using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Desktop.History;
using GitExtensions.Desktop.Services;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  The Changes tab of the main window (like GitHub Desktop): one list of the changed files with a check box per file
///  (checked = staged), the diff of the selected file, and a summary / description / commit button under the list.
/// </summary>
public sealed partial class WorkingChangesViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly GitModule _module;
    private readonly CommitWindowStore _store;
    private int _refreshVersion;
    private bool _syncingMessage;
    private bool _syncingSelection;
    private bool _disposed;
    private Func<Task>? _pendingConfirmation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    private string? _branch;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The configured git user, shown as the avatar chip next to the summary.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorInitials))]
    [NotifyPropertyChangedFor(nameof(AuthorBrush))]
    private string _author = string.Empty;

    /// <summary>The question of the confirmation bar (discard files, discard the stash), or null when none is asked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    private string? _confirmationText;

    [ObservableProperty]
    private string _confirmButtonText = "Discard";

    public WorkingChangesViewModel(MainWindowViewModel main, GitModule module, IMessageDialogs messages, CommitWindowStore? store = null)
    {
        _main = main;
        _module = module;
        _store = store ?? CommitWindowStore.Current;
        WorkingDir = module.WorkingDir;

        Commit = new CommitViewModel(module, AllChanges, messages, () => Task.CompletedTask);
        Commit.Committed += _store.AddRecent;
        Commit.PropertyChanged += OnCommitPropertyChanged;
        Diff = new FileDiffViewModel(module, AfterIndexChangeAsync, ReportError);
        Resolver = new ConflictResolverViewModel(() => _module, AfterIndexChangeAsync, ReportError);
        Stashes = new StashViewModel(() => _disposed ? null : _module, AfterStashChangeAsync, ReportError, message => _main.Status = message);
    }

    /// <summary>The working directory of the repository the tab commits to.</summary>
    public string WorkingDir { get; }

    public CommitViewModel Commit { get; }

    public FileDiffViewModel Diff { get; }

    public ConflictResolverViewModel Resolver { get; }

    /// <summary>All changes as git reports them (a partly staged file appears twice), unfiltered.</summary>
    public RangeObservableCollection<ChangedFileViewModel> AllChanges { get; } = [];

    /// <summary>The changed files shown in the list, one row per path, filtered.</summary>
    public RangeObservableCollection<WorkingFileRowViewModel> Rows { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasNoChanges => AllChanges.Count == 0;

    public bool IsConfirming => ConfirmationText is not null;

    public string AuthorInitials => AvatarStyle.Initials(Author);

    public Avalonia.Media.IBrush AuthorBrush => Views.AvatarBrushes.For(AvatarStyle.ColorIndex(Author));

    public string ChangedFilesHeader
    {
        get
        {
            int count = Rows.Count;
            return $"{count} changed {(count == 1 ? "file" : "files")}";
        }
    }

    /// <summary>The state of the "select all" check box: every row checked, none, or a mix.</summary>
    public bool? AllIncluded
    {
        get
        {
            if (Rows.Count == 0 || Rows.All(r => r.IsIncluded == false))
            {
                return false;
            }

            return Rows.All(r => r.IsIncluded == true) ? true : null;
        }
    }

    public string CommitButtonText => string.IsNullOrEmpty(Branch) ? "Commit" : $"Commit to {Branch}";

    /// <summary>The selected rows (Ctrl and Shift select several), in list order.</summary>
    public IReadOnlyList<WorkingFileRowViewModel> SelectedRows => [.. Rows.Where(r => r.IsSelected)];

    /// <summary>The selected row when exactly one is selected; setting it selects only that row.</summary>
    public WorkingFileRowViewModel? SelectedRow
    {
        get => SelectedRows is [{ } only] ? only : null;
        set => SelectOnly(value);
    }

    public bool HasMultipleSelection => Rows.Count(r => r.IsSelected) > 1;

    public string MultipleSelectionText => $"{Rows.Count(r => r.IsSelected)} files selected";

    /// <summary>
    ///  Reads the pending merge message and the changes.
    /// </summary>
    public async Task LoadAsync()
    {
        await Commit.LoadAsync();
        await RefreshAsync();
    }

    /// <summary>
    ///  Re-reads the changed files and the stashes, keeping the selected files when they are still changed. Concurrent
    ///  calls: the newest wins.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        int version = ++_refreshVersion;
        IsBusy = true;
        try
        {
            (List<ChangedFileViewModel> changes, string? error, string branch, string author) = await Task.Run(() =>
            {
                (List<GitItemStatus> items, string? statusError) = GitErrorMessages.SplitStatusError(_module.GetAllChangedFiles(), _module.WorkingDir);
                return (items.Select(item => new ChangedFileViewModel(item)).ToList(), statusError, _module.GetSelectedBranch(), _module.GetEffectiveSetting("user.name"));
            });

            if (version != _refreshVersion || _disposed)
            {
                return;
            }

            Branch = branch;
            Author = author;
            if (error is not null)
            {
                ReportError(error);
            }

            AllChanges.ReplaceAll(changes);
            OnPropertyChanged(nameof(HasNoChanges));
            await RebuildRowsAsync();
            await LoadStashesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportError($"Could not read the changes of {_module.WorkingDir}: {ex.Message}");
        }
        finally
        {
            if (version == _refreshVersion)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    ///  Shows a status the main window just read (after staging, unstaging or an automatic refresh) instead of running
    ///  git status, the branch, the user name and the stash list again: one click used to run about eight git commands.
    /// </summary>
    public async Task ShowStatusAsync(IReadOnlyList<ChangedFileViewModel> changes)
    {
        if (_disposed)
        {
            return;
        }

        ++_refreshVersion;
        AllChanges.ReplaceAll(changes);
        OnPropertyChanged(nameof(HasNoChanges));
        await RebuildRowsAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        Commit.PropertyChanged -= OnCommitPropertyChanged;
        Commit.Committed -= _store.AddRecent;
        Commit.Dispose();
    }

    /// <summary>Selects exactly <paramref name="row"/> (none for <see langword="null"/>) and shows its diff.</summary>
    public void SelectOnly(WorkingFileRowViewModel? row)
    {
        _syncingSelection = true;
        try
        {
            foreach (WorkingFileRowViewModel each in Rows)
            {
                each.IsSelected = ReferenceEquals(each, row);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        SelectionSyncRequested?.Invoke();
        OnSelectionChanged(row is not null);
    }

    /// <summary>
    ///  Raised when the view-model changed the selection itself (select only, select all, rows rebuilt), so the list shows it.
    /// </summary>
    public event Action? SelectionSyncRequested;

    /// <summary>
    ///  Takes the selection of the list. The list is the source of truth for clicks: binding each item container's
    ///  IsSelected loses deselections of rows scrolled out of view (their containers are recycled), which left stale rows
    ///  selected ("3 files selected" after one click).
    /// </summary>
    public void ApplyViewSelection(IReadOnlyCollection<WorkingFileRowViewModel> selected)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            foreach (WorkingFileRowViewModel each in Rows)
            {
                each.IsSelected = selected.Contains(each);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        OnSelectionChanged(selected.Count > 0);
    }

    /// <summary>Selects every listed file.</summary>
    public void SelectAll()
    {
        _syncingSelection = true;
        try
        {
            foreach (WorkingFileRowViewModel each in Rows)
            {
                each.IsSelected = true;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        SelectionSyncRequested?.Invoke();
        OnSelectionChanged(Rows.Count > 0);
    }

    /// <summary>Stages (includes) the selected files.</summary>
    [RelayCommand]
    public Task IncludeSelectedAsync() => StageAsync([.. SelectedRows.Select(r => r.Unstaged).OfType<ChangedFileViewModel>()]);

    /// <summary>Unstages (excludes) the selected files.</summary>
    [RelayCommand]
    public Task ExcludeSelectedAsync() => UnstageAsync([.. SelectedRows.Select(r => r.Staged).OfType<ChangedFileViewModel>()]);

    /// <summary>Asks to throw away the changes of the selected files (shown as a confirmation bar).</summary>
    [RelayCommand]
    public void DiscardSelected() => BeginDiscard(SelectedRows);

    /// <summary>Asks to throw away the changes of every listed file.</summary>
    [RelayCommand]
    public void DiscardAll() => BeginDiscard([.. Rows]);

    public void BeginDiscard(IReadOnlyList<WorkingFileRowViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        List<WorkingFileRowViewModel> copy = [.. rows];
        Confirm(
            copy.Count == 1
                ? $"Discard all changes to {copy[0].Name}? This cannot be undone."
                : $"Discard all changes to {copy.Count} files? This cannot be undone.",
            copy.Count == 1 ? "Discard" : $"Discard {copy.Count} files",
            () => DiscardAsync(copy));
    }

    /// <summary>Appends ignore patterns (e.g. "/path/file" or "*.log") to the .gitignore of the repository and refreshes.</summary>
    public async Task AddToGitIgnoreAsync(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => WorkingTreeIndex.AppendToGitIgnore(WorkingDir, patterns));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError($"Could not update .gitignore: {ex.Message}");
        }

        await AfterIndexChangeAsync();
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        Func<Task>? action = _pendingConfirmation;
        CancelConfirmation();
        if (action is not null)
        {
            await action();
        }
    }

    [RelayCommand]
    private void CancelConfirmation()
    {
        _pendingConfirmation = null;
        ConfirmationText = null;
    }

    private void Confirm(string question, string button, Func<Task> action)
    {
        _pendingConfirmation = action;
        ConfirmButtonText = button;
        ConfirmationText = question;
    }

    /// <summary>Checks or unchecks every listed file: stages them all, or unstages them all when all are checked.</summary>
    [RelayCommand]
    private Task ToggleAllAsync()
        => AllIncluded == true
            ? UnstageAsync([.. Rows.Select(r => r.Staged).OfType<ChangedFileViewModel>()])
            : StageAsync([.. Rows.Select(r => r.Unstaged).OfType<ChangedFileViewModel>()]);

    // The check box of a selected row acts on the whole selection, like in GitHub Desktop.
    private Task ToggleRowAsync(WorkingFileRowViewModel row)
    {
        IReadOnlyList<WorkingFileRowViewModel> rows = row.IsSelected ? SelectedRows : [row];
        bool include = row.IsIncluded != true;

        // Show the new state at once, like GitHub Desktop; the refresh after git ran confirms it (or puts it back).
        foreach (WorkingFileRowViewModel each in rows)
        {
            each.IsIncluded = include;
        }

        OnPropertyChanged(nameof(AllIncluded));
        return include
            ? StageAsync([.. rows.Select(r => r.Unstaged).OfType<ChangedFileViewModel>()])
            : UnstageAsync([.. rows.Select(r => r.Staged).OfType<ChangedFileViewModel>()]);
    }

    private bool CanCommit()
        => !Commit.IsCommitting && !string.IsNullOrWhiteSpace(Summary) && (Commit.StagedCount > 0 || (Commit.Amend && Commit.HasHead));

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        ErrorText = null;
        if (!await Commit.TryCommitAsync())
        {
            ErrorText = Commit.ErrorText;
            return;
        }

        // Reloads history and changes (this tab included) and selects the new commit.
        await _main.ReloadAfterCommitAsync();
    }

    partial void OnFilterChanged(string value) => _ = RebuildRowsAsync();

    partial void OnSummaryChanged(string value) => PushMessage();

    partial void OnDescriptionChanged(string value) => PushMessage();

    private void OnRowSelectionChanged(WorkingFileRowViewModel row)
    {
        if (!_syncingSelection)
        {
            OnSelectionChanged(row.IsSelected);
        }
    }

    private void OnSelectionChanged(bool selectedSomething)
    {
        if (selectedSomething)
        {
            IsShowingStash = false;
        }

        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(MultipleSelectionText));
        _ = ShowSelectedAsync();
    }

    // The commit view-model keeps one message: the summary is its first line, the description the rest.
    private void PushMessage()
    {
        if (_syncingMessage)
        {
            return;
        }

        _syncingMessage = true;
        try
        {
            string summary = Summary.Trim();
            string description = Description.Trim('\r', '\n');
            Commit.Message = description.Length == 0 ? summary : $"{summary}\n\n{description}";
        }
        finally
        {
            _syncingMessage = false;
        }
    }

    // A message set by the commit view-model itself (a pending merge message, the previous message for amend, cleared
    // after a commit) is split back into summary and description.
    private void OnCommitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommitViewModel.Message) when !_syncingMessage:
                _syncingMessage = true;
                try
                {
                    (Summary, Description) = Split(Commit.Message);
                }
                finally
                {
                    _syncingMessage = false;
                }

                break;
            case nameof(CommitViewModel.StagedCount) or nameof(CommitViewModel.IsCommitting) or nameof(CommitViewModel.Amend) or nameof(CommitViewModel.HasHead):
                CommitCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    /// <summary>Splits a commit message into its first line (the summary) and the rest (the description).</summary>
    public static (string Summary, string Description) Split(string message)
    {
        string normalized = message.Replace("\r\n", "\n");
        int newline = normalized.IndexOf('\n');
        return newline < 0
            ? (normalized.Trim(), string.Empty)
            : (normalized[..newline].Trim(), normalized[(newline + 1)..].Trim('\n'));
    }

    private async Task RebuildRowsAsync()
    {
        string filter = Filter.Trim();
        HashSet<string> selected = [.. Rows.Where(r => r.IsSelected).Select(r => r.Name)];
        List<WorkingFileRowViewModel> rows = [.. AllChanges
            .Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Name)
            .Select(g => new WorkingFileRowViewModel(g.FirstOrDefault(c => !c.IsStaged), g.FirstOrDefault(c => c.IsStaged), ToggleRowAsync, OnRowSelectionChanged))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

        // Keep the same files selected (new row objects, so the diff is reloaded: a file may have moved to the index).
        _syncingSelection = true;
        try
        {
            foreach (WorkingFileRowViewModel row in rows)
            {
                row.IsSelected = selected.Contains(row.Name);
            }

            Rows.ReplaceAll(rows);
        }
        finally
        {
            _syncingSelection = false;
        }

        SelectionSyncRequested?.Invoke();

        OnPropertyChanged(nameof(ChangedFilesHeader));
        OnPropertyChanged(nameof(AllIncluded));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(MultipleSelectionText));
        await ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        ChangedFileViewModel? file = SelectedRow?.Shown;
        await Resolver.ShowAsync(file, AllChanges);
        await Diff.ShowAsync(file);
    }

    private Task StageAsync(IReadOnlyList<ChangedFileViewModel> files)
        => ChangeIndexAsync(module => WorkingTreeIndex.Stage(module, files), files.Count > 0);

    private Task UnstageAsync(IReadOnlyList<ChangedFileViewModel> files)
        => ChangeIndexAsync(module => WorkingTreeIndex.Unstage(module, files), files.Count > 0);

    private async Task ChangeIndexAsync(Func<GitModule, string?> change, bool any)
    {
        if (!any)
        {
            return;
        }

        ErrorText = null;
        try
        {
            string? error = await Task.Run(() => change(_module));
            if (error is not null)
            {
                ReportError($"Failed to update the index: {GitErrorMessages.Describe(string.IsNullOrWhiteSpace(error) ? "git reported an error." : error, _module.WorkingDir)}");
            }
        }
        catch (Exception ex)
        {
            ReportError($"Failed to update the index: {ex.Message}");
        }

        await AfterIndexChangeAsync();
    }

    private async Task DiscardAsync(IReadOnlyList<WorkingFileRowViewModel> rows)
    {
        ErrorText = null;
        try
        {
            string? error = await Task.Run(() => WorkingTreeReset.ResetFiles(_module, rows.Select(r => r.ForDiscard.Item)));
            if (error is not null)
            {
                ReportError($"Failed to discard the changes: {error}");
            }
        }
        catch (Exception ex)
        {
            ReportError($"Failed to discard the changes: {ex.Message}");
        }

        await AfterIndexChangeAsync();
    }

    // The main window re-reads its changes and refreshes this tab with them (see MainWindowViewModel.RefreshChangesAsync).
    private Task AfterIndexChangeAsync() => _main.RefreshChangesAsync();

    private void ReportError(string message)
    {
        ErrorText = message;
        _main.ErrorMessage = message;
    }
}
