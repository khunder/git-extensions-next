using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Desktop.ViewModels;

// The bottom tab control (Commit | Diff | File tree | Console | Build Report) follows the grid selection.
public sealed partial class MainWindowViewModel
{
    private RevisionTabsViewModel? _revisionTabs;

    // IsWorkingTreeSelected (the "Uncommitted changes" pseudo row) is declared in MainWindowViewModel.Grid.cs.

    /// <summary>The tabs below the revision grid. Created on first use; they then follow the selection.</summary>
    public RevisionTabsViewModel RevisionTabs => _revisionTabs ?? CreateRevisionTabs();

    // The commits of the loaded history that have the given one as a parent.
    private IReadOnlyList<ObjectId> GetChildren(ObjectId id)
        => [.. Commits.Where(commit => commit.Revision.ParentIds is { } parents && parents.Contains(id)).Select(commit => commit.ObjectId)];

    private RevisionTabsViewModel CreateRevisionTabs()
    {
        RevisionTabsViewModel tabs = new((path, blame, revision) => FileHistory.OpenAsync(path, blame, revision), options: ViewOptions);
        _revisionTabs = tabs;
        tabs.Diff.WorkingTreeChanged = RefreshChangesAsync;
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RevisionTabsViewModel.SelectedTabIndex))
            {
                OnPropertyChanged(nameof(IsBrowsingAllFiles));
                OnPropertyChanged(nameof(IsShowingChangedFiles));
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedCommit) or nameof(IsWorkingTreeSelected))
            {
                SyncRevisionTabs();
            }
        };
        SyncRevisionTabs();
        return tabs;
    }

    private void SyncRevisionTabs()
    {
        if (_revisionTabs is not { } tabs)
        {
            return;
        }

        if (_module is null)
        {
            tabs.Clear();
        }
        else if (IsWorkingTreeSelected)
        {
            tabs.ShowWorkingTree(_module);
        }
        else if (SelectedCommit is { } commit)
        {
            tabs.ShowCommit(_module, commit.Revision);
        }
        else
        {
            tabs.Clear();
        }
    }
}
