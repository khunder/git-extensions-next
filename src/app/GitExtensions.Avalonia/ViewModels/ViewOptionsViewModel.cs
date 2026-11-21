using CommunityToolkit.Mvvm.ComponentModel;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>The options of the View menu (tabs, commit info layout, split layout, filter bar); every change is persisted.</summary>
public sealed partial class ViewOptionsViewModel : ObservableObject
{
    private readonly ViewOptionsStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommitInfoBelow), nameof(IsCommitInfoLeft), nameof(IsCommitInfoRight))]
    private CommitInfoLayout _commitInfoPosition;

    [ObservableProperty]
    private bool _sideBySide;

    [ObservableProperty]
    private bool _showCommitTab;

    [ObservableProperty]
    private bool _showDiffTab;

    [ObservableProperty]
    private bool _showFileTreeTab;

    [ObservableProperty]
    private bool _showConsoleTab;

    [ObservableProperty]
    private bool _showBuildReportTab;

    [ObservableProperty]
    private bool _showFilterBar;

    /// <summary>Changes | History tabs on top of the sidebar, with a GitHub Desktop style Changes tab (off: the classic sidebar).</summary>
    [ObservableProperty]
    private bool _showChangesTab;

    /// <summary>The simple mode: a compact, GitHub Desktop style window with only everyday actions in plain words.</summary>
    [ObservableProperty]
    private bool _simpleMode;

    /// <summary>The simple mode's History shows the full graph (branches tree, revision grid, tabs) instead of the plain list.</summary>
    [ObservableProperty]
    private bool _simpleHistoryGraph;

    private bool _loading;

    public ViewOptionsViewModel(ViewOptionsStore store)
    {
        _store = store;
        Settings = store.Load();
        _loading = true;
        CommitInfoPosition = Settings.CommitInfoPosition;
        SideBySide = Settings.SideBySide;
        ShowCommitTab = Settings.ShowCommitTab;
        ShowDiffTab = Settings.ShowDiffTab;
        ShowFileTreeTab = Settings.ShowFileTreeTab;
        ShowConsoleTab = Settings.ShowConsoleTab;
        ShowBuildReportTab = Settings.ShowBuildReportTab;
        ShowFilterBar = Settings.ShowFilterBar;
        ShowChangesTab = Settings.ShowChangesTab;
        SimpleMode = Settings.SimpleMode;
        SimpleHistoryGraph = Settings.SimpleHistoryGraph;
        _loading = false;
    }

    /// <summary>The settings as last loaded or saved (the remote branches and tags flags are kept here for the main view-model).</summary>
    public ViewOptionsSettings Settings { get; private set; }

    public bool IsCommitInfoBelow => CommitInfoPosition == CommitInfoLayout.Below;

    public bool IsCommitInfoLeft => CommitInfoPosition == CommitInfoLayout.Left;

    public bool IsCommitInfoRight => CommitInfoPosition == CommitInfoLayout.Right;

    /// <summary>Saves the current options together with the two ref flags that live on the main view-model.</summary>
    public void Save(bool showRemoteBranches, bool showTags)
    {
        Settings = new ViewOptionsSettings(
            CommitInfoPosition, SideBySide, ShowCommitTab, ShowDiffTab, ShowFileTreeTab, ShowConsoleTab, ShowBuildReportTab, showRemoteBranches, showTags, ShowFilterBar, ShowChangesTab, SimpleMode, SimpleHistoryGraph);
        _store.Save(Settings);
    }

    /// <summary>Raised after an option changed (not while loading).</summary>
    public event Action? OptionChanged;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(IsCommitInfoBelow) or nameof(IsCommitInfoLeft) or nameof(IsCommitInfoRight)))
        {
            OptionChanged?.Invoke();
        }
    }
}
