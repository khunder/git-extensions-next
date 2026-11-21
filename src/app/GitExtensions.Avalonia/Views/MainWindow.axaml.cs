using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;
using GitExtensions.Desktop.Views.ContextMenus;

namespace GitExtensions.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        MainColumns.ColumnDefinitions[2].MinWidth = MinRightAreaWidth;
        this.FindControl<GridSplitter>("SidebarSplitter")!.DoubleTapped += (_, _) => SidebarCollapsed = true;
        AddHandler(Thumb.DragCompletedEvent, (_, _) => SaveLayout());
        _layout = new LayoutSettings();
        ApplyLayout(_layout);
        InitializeShortcuts();
        InitializeViewOptions();
        InitializeDialogs();

        // The simple toolbar's "Current repository" segment lines up with the sidebar, also while the splitter is dragged.
        SidebarHost.SizeChanged += (_, e) => SimpleToolbar.AlignRepositorySegment(e.NewSize.Width + SidebarSplitter.Bounds.Width);

        // The simple history list offers the same commit menu (and its key shortcuts) as the full revision grid.
        MenuKit.AttachBuilt(
            SimpleHistoryList,
            row => row is CommitItemViewModel commit && DataContext is MainWindowViewModel viewModel
                ? RevisionGridContextMenu.Build(viewModel, commit, viewModel.Actions.Clipboard ?? new TopLevelClipboardService(this))
                : null);
        SimpleHistoryList.KeyDown += (_, e) =>
        {
            if (DataContext is MainWindowViewModel viewModel && RevisionGridContextMenu.TryHandleKey(viewModel, e))
            {
                e.Handled = true;
            }
        };
    }

    private const double MinRightAreaWidth = 280;

    private LayoutStore? _layoutStore;
    private LayoutSettings _layout;
    private double _expandedSidebarWidth = LayoutSettings.DefaultSidebarWidth;

    /// <summary>
    ///  Whether the branches sidebar is hidden. Setting it also hides its splitter and updates the toggle button.
    /// </summary>
    public bool SidebarCollapsed
    {
        get => _layout.SidebarCollapsed;
        set
        {
            if (value && !_layout.SidebarCollapsed)
            {
                _expandedSidebarWidth = Math.Max(LayoutSettings.MinSidebarWidth, MainColumns.ColumnDefinitions[0].ActualWidth);
            }

            _layout = _layout with { SidebarCollapsed = value };
            ShowSidebar(!value, _expandedSidebarWidth);
            SaveLayout();
        }
    }

    /// <summary>
    ///  Restores the stored layout and keeps saving it (after a splitter drag, a sidebar toggle and on closing).
    /// </summary>
    public void UseLayoutStore(LayoutStore store)
    {
        _layoutStore = store;
        ApplyLayout(store.Load());
        Closing += (_, _) => SaveLayout();
    }

    /// <summary>
    ///  Applies <paramref name="settings"/> (clamped) to the splitter-controlled columns and rows.
    /// </summary>
    public void ApplyLayout(LayoutSettings settings)
    {
        _layout = settings.Clamp();
        RowDefinitions rows = BrowseRows.RowDefinitions;
        rows[0].Height = new GridLength(_layout.GridHeight, GridUnitType.Star);
        rows[2].Height = new GridLength(_layout.TabsHeight, GridUnitType.Star);

        // Keep the sidebar from squeezing the other two columns off a small window.
        double sidebar = _layout.SidebarWidth;
        if (Width > 0)
        {
            sidebar = Math.Min(sidebar, Math.Max(LayoutSettings.MinSidebarWidth, Width * 0.3));
        }

        _expandedSidebarWidth = sidebar;
        ShowSidebar(!_layout.SidebarCollapsed, sidebar);
    }

    /// <summary>
    ///  Reads the current sizes from the live layout (keeping the stored values for anything not laid out yet).
    /// </summary>
    public LayoutSettings CaptureLayout()
    {
        LayoutSettings captured = _layout;
        if (!_layout.SidebarCollapsed && MainColumns.ColumnDefinitions[0].ActualWidth > 0)
        {
            captured = captured with { SidebarWidth = MainColumns.ColumnDefinitions[0].ActualWidth };
        }
        else if (_layout.SidebarCollapsed)
        {
            captured = captured with { SidebarWidth = _expandedSidebarWidth };
        }

        RowDefinitions rows = BrowseRows.RowDefinitions;
        if (rows[0].ActualHeight > 0 && rows[2].ActualHeight > 0)
        {
            captured = captured with { GridHeight = rows[0].ActualHeight, TabsHeight = rows[2].ActualHeight };
        }

        return captured.Clamp();
    }

    private void ShowSidebar(bool visible, double width)
    {
        ColumnDefinition column = MainColumns.ColumnDefinitions[0];
        column.MinWidth = visible ? LayoutSettings.MinSidebarWidth : 0;
        column.MaxWidth = visible ? double.PositiveInfinity : 0;
        column.Width = new GridLength(visible ? width : 0);
        this.FindControl<Control>("SidebarHost")!.IsVisible = visible;
        this.FindControl<Control>("SidebarSplitter")!.IsVisible = visible;
    }

    private void SaveLayout()
    {
        _layout = CaptureLayout();
        _layoutStore?.Save(_layout);
    }
}
