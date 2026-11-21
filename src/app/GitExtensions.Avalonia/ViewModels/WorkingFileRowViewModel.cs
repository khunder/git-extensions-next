using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  One file of the Changes tab: the unstaged and the staged change of the same path merged into a single row, with a
///  check box that says whether the file goes into the next commit (checked: staged, unchecked: unstaged, mixed: both).
/// </summary>
public sealed partial class WorkingFileRowViewModel : ObservableObject
{
    private readonly Action<WorkingFileRowViewModel>? _selectionChanged;

    /// <summary>Selected in the list (several rows can be selected with Ctrl and Shift).</summary>
    [ObservableProperty]
    private bool _isSelected;

    public WorkingFileRowViewModel(
        ChangedFileViewModel? unstaged,
        ChangedFileViewModel? staged,
        Func<WorkingFileRowViewModel, Task> toggle,
        Action<WorkingFileRowViewModel>? selectionChanged = null)
    {
        Unstaged = unstaged;
        Staged = staged;
        _selectionChanged = selectionChanged;
        ChangedFileViewModel shown = unstaged ?? staged ?? throw new ArgumentException("A row needs at least one change.");
        Name = shown.Name;
        FileName = shown.FileName;
        Directory = shown.Directory;
        DirectoryPrefix = shown.Directory.Length == 0 ? string.Empty : shown.Directory + "/";
        Status = shown.Status;
        Description = shown.Description;
        IsConflicted = (unstaged?.IsConflicted ?? false) || (staged?.IsConflicted ?? false);
        _isIncluded = (unstaged, staged) switch
        {
            (null, not null) => true,
            (not null, null) => false,
            _ => null
        };
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
    }

    /// <summary>The work tree side of the file (not yet staged), if any.</summary>
    public ChangedFileViewModel? Unstaged { get; }

    /// <summary>The index side of the file (staged), if any.</summary>
    public ChangedFileViewModel? Staged { get; }

    /// <summary>The change whose diff is shown when the row is selected: the work tree side first.</summary>
    public ChangedFileViewModel Shown => Unstaged ?? Staged!;

    /// <summary>The change to throw away when the file is discarded: the index side first, so a staged new file is removed.</summary>
    public ChangedFileViewModel ForDiscard => Staged ?? Unstaged!;

    public string Name { get; }

    public string FileName { get; }

    public string Directory { get; }

    /// <summary>The folder with a trailing slash (empty at the top level), shown dimmed in front of the file name.</summary>
    public string DirectoryPrefix { get; }

    public string Status { get; }

    public bool IsAdded => Status is "A" or "C";

    public bool IsDeleted => Status == "D";

    public bool IsModified => Status == "M";

    public bool IsRenamed => Status == "R";

    public string Description { get; }

    public bool IsConflicted { get; }

    /// <summary>
    ///  <see langword="true"/> when fully staged, <see langword="false"/> when not staged, <see langword="null"/> when partly
    ///  staged. Set right away when the check box is clicked, before git has run, so the click feels instant.
    /// </summary>
    [ObservableProperty]
    private bool? _isIncluded;

    /// <summary>Stages the file (unchecked or mixed) or unstages it (checked); with the whole selection when the row is part of it.</summary>
    public IAsyncRelayCommand ToggleCommand { get; }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke(this);
}
