using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Desktop.ViewModels;

namespace GitExtensions.Desktop.Views;

public sealed partial class SimpleToolbarView : UserControl
{
    public SimpleToolbarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.BranchPickerRequested -= OpenBranchPicker;
            }

            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel is not null)
            {
                _viewModel.BranchPickerRequested += OpenBranchPicker;
            }
        };
    }

    /// <summary>The current branch is bold in the branch drop-down.</summary>
    public static FuncValueConverter<bool, FontWeight> CurrentWeight { get; } = new(isCurrent => isCurrent ? FontWeight.SemiBold : FontWeight.Normal);

    private MainWindowViewModel? _viewModel;

    // Like GitHub Desktop the repository list covers the sidebar: as wide as the segment, down to the bottom of the window.
    private void OnRepositoryFlyoutOpening(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top && SimpleRepositoryButton.TranslatePoint(new Point(0, SimpleRepositoryButton.Bounds.Height), top) is { } below)
        {
            RepositoryPanel.Width = Math.Max(320, SimpleRepositoryButton.Bounds.Width);
            RepositoryPanel.Height = Math.Max(240, top.ClientSize.Height - below.Y - 12);
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RepositoryFilter = string.Empty;
            _ = viewModel.LoadRepositoryListAsync();
        }

        Dispatcher.UIThread.Post(() => SimpleRepositoryFilter.Focus(), DispatcherPriority.Input);
    }

    // The Branch menu's "Merge into current branch…", "Rebase…" and "Delete…" choose their branch in the drop-down.
    private void OpenBranchPicker() => SimpleBranchButton.Flyout?.ShowAt(SimpleBranchButton);

    /// <summary>
    ///  Makes the "Current repository" segment as wide as the sidebar below it (sidebar plus splitter), like GitHub Desktop,
    ///  so the toolbar and the Changes | History panel share one left edge column.
    /// </summary>
    public void AlignRepositorySegment(double width)
    {
        if (width > 0)
        {
            SegmentColumns.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(width);
        }
    }

    // A choice in a drop-down closes it, like a menu.
    // Posted: Click is raised before the button runs its Command, and hiding the drop-down first takes the button out of
    // the tree, which clears its Command binding, so the click did nothing (switching repositories was broken).
    private void OnFlyoutChoice(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            foreach (Button owner in new[] { SimpleRepositoryButton, SimpleBranchButton })
            {
                if (owner.Flyout is FlyoutBase { IsOpen: true } flyout)
                {
                    flyout.Hide();
                }
            }
        });

    // Closing the branch drop-down forgets a half-done merge choice and the filter. Posted: a branch click closes the
    // drop-down before its command runs, and the command must still see the merge choice.
    private void OnBranchFlyoutClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            Dispatcher.UIThread.Post(() =>
            {
                viewModel.IsMergePicker = false;
                viewModel.BranchFilter = string.Empty;
            });
        }
    }
}
