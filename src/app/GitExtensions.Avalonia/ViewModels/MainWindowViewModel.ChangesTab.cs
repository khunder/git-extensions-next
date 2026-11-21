using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  The optional Changes | History tabs at the top of the sidebar (View > "Changes tab in the sidebar", like GitHub
///  Desktop). History shows the branches tree with the revision grid; Changes shows the working tree files with a commit
///  box, and the diff of the selected file. With the option off the sidebar is the classic branches tree.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public const int ChangesTabIndex = 0;
    public const int HistoryTabIndex = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTabSelected))]
    private int _sidebarTabIndex = HistoryTabIndex;

    [ObservableProperty]
    private WorkingChangesViewModel? _changesTab;

    /// <summary>The Changes | History tabs are enabled in the View menu.</summary>
    public bool IsChangesTabEnabled => ViewOptions.ShowChangesTab || ViewOptions.SimpleMode;

    public bool IsChangesTabSelected => IsChangesTabEnabled && SidebarTabIndex == ChangesTabIndex;

    public bool IsHistoryTabSelected => !IsChangesTabSelected;

    /// <summary>The number of changed files, shown next to the Changes tab header.</summary>
    public string ChangesTabCountText => ChangedFiles.Count == 0 ? string.Empty : ChangedFiles.Count.ToString(CultureInfo.InvariantCulture);

    public bool HasChangesTabCount => ChangedFiles.Count > 0;

    /// <summary>Switches the sidebar to the given tab and shows the sidebar when it is collapsed.</summary>
    private void SelectSidebarTab(int index)
    {
        SidebarTabIndex = index;
        UiRequested?.Invoke(UiTarget.ShowSidebar);
    }

    partial void OnChangesTabChanged(WorkingChangesViewModel? oldValue, WorkingChangesViewModel? newValue) => oldValue?.Dispose();

    // The tab is only built (and git status only run for it) while it is shown.
    partial void OnSidebarTabIndexChanged(int value) => EnsureChangesTab();

    private void InitializeChangesTab()
    {
        ChangedFiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ChangesTabCountText));
            OnPropertyChanged(nameof(HasChangesTabCount));
        };
        ViewOptions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewOptionsViewModel.ShowChangesTab))
            {
                OnChangesTabEnabledChanged();
            }
        };
    }

    // Turning the option off goes back to the classic sidebar (History) and drops the tab with its git state.
    private void OnChangesTabEnabledChanged()
    {
        if (!IsChangesTabEnabled)
        {
            SidebarTabIndex = HistoryTabIndex;
            ChangesTab = null;
        }

        OnPropertyChanged(nameof(IsChangesTabEnabled));
        OnPropertyChanged(nameof(IsChangesTabSelected));
        OnPropertyChanged(nameof(IsHistoryTabSelected));
        EnsureChangesTab();
    }

    /// <summary>
    ///  Creates the Changes tab for the open repository, or refreshes the existing one when it belongs to it (keeping a
    ///  summary being typed across reloads such as a checkout).
    /// </summary>
    private void EnsureChangesTab()
    {
        if (_module is not { } module || !IsChangesTabSelected)
        {
            return;
        }

        if (ChangesTab is { } existing && string.Equals(existing.WorkingDir, module.WorkingDir, StringComparison.Ordinal))
        {
            _ = existing.RefreshAsync();
            return;
        }

        WorkingChangesViewModel tab = new(this, module, _git.Messages);
        ChangesTab = tab;
        _ = tab.LoadAsync();
    }

    /// <summary>Forgets the Changes tab (the repository was closed or another one is opened).</summary>
    private void ResetChangesTab(string? keepWorkingDir = null)
    {
        if (ChangesTab is { } tab && !string.Equals(tab.WorkingDir, keepWorkingDir, StringComparison.Ordinal))
        {
            ChangesTab = null;
        }
    }
}
