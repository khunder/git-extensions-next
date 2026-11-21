using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;
using GitExtensions.Desktop.Views.ContextMenus;

namespace GitExtensions.Desktop.Views;

/// <summary>
///  The Changes tab of the sidebar: the changed files with a check box each (checked = staged) and the commit box.
///  Files are selected the standard way (click, Ctrl+click, Shift+click, Ctrl+A); the check boxes and the context menu act
///  on the whole selection.
/// </summary>
public sealed partial class ChangesSidebarView : UserControl
{
    public ChangesSidebarView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        MenuKit.AttachBuilt(FileList, row => row is WorkingFileRowViewModel file && DataContext is WorkingChangesViewModel viewModel ? BuildFileMenu(viewModel, file) : null);
        FileList.SelectionChanged += OnListSelectionChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private WorkingChangesViewModel? _viewModel;
    private bool _applyingSelection;

    // The list's selection model knows every selected row, also those scrolled out of view: it is the source of truth.
    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_applyingSelection && DataContext is WorkingChangesViewModel viewModel)
        {
            viewModel.ApplyViewSelection([.. FileList.SelectedItems?.OfType<WorkingFileRowViewModel>() ?? []]);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.SelectionSyncRequested -= ShowViewModelSelection;
        }

        _viewModel = DataContext as WorkingChangesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.SelectionSyncRequested += ShowViewModelSelection;
            ShowViewModelSelection();
        }
    }

    // The view-model selected rows itself (select only, select all, rows rebuilt after a refresh): show that in the list.
    private void ShowViewModelSelection()
    {
        if (_viewModel is not { } viewModel || FileList.Selection is not { } selection)
        {
            return;
        }

        _applyingSelection = true;
        try
        {
            selection.BeginBatchUpdate();
            selection.Clear();
            for (int i = 0; i < viewModel.Rows.Count; i++)
            {
                if (viewModel.Rows[i].IsSelected)
                {
                    selection.Select(i);
                }
            }

            selection.EndBatchUpdate();
        }
        finally
        {
            _applyingSelection = false;
        }
    }

    /// <summary>Puts the keyboard focus into the summary box.</summary>
    public void FocusSummary() => SummaryBox.Focus();

    /// <summary>Puts the keyboard focus into the filter box.</summary>
    public void FocusFilter() => FilterBox.Focus();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not WorkingChangesViewModel viewModel)
        {
            return;
        }

        bool inList = FileList.IsKeyboardFocusWithin && e.Source is not TextBox;

        // Ctrl+Enter commits from anywhere in the tab, like in the Commit window.
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (viewModel.CommitCommand.CanExecute(null))
            {
                viewModel.CommitCommand.Execute(null);
            }

            e.Handled = true;
        }

        // Space checks or unchecks the selected files while the list has the focus.
        else if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && inList && viewModel.SelectedRows is [{ } first, ..])
        {
            first.ToggleCommand.Execute(null);
            e.Handled = true;
        }

        // Delete asks to discard the selected files.
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && inList)
        {
            viewModel.DiscardSelected();
            e.Handled = true;
        }
    }

    // The menu of the Commit window's file lists (open, show in folder, history, blame, copy paths, .gitignore, …) for the
    // selection, with the GitHub Desktop style actions of the tab in front: discard, include/exclude, ignore, stash.
    private List<Control> BuildFileMenu(WorkingChangesViewModel viewModel, WorkingFileRowViewModel row)
    {
        // A right click on a row outside the selection selects that row only.
        if (!row.IsSelected)
        {
            viewModel.SelectOnly(row);
        }

        IReadOnlyList<WorkingFileRowViewModel> selection = viewModel.SelectedRows is { Count: > 0 } selected ? selected : [row];
        int count = selection.Count;
        string files = count == 1 ? "file" : $"{count} selected files";
        bool anyUnstaged = selection.Any(r => r.Unstaged is not null);
        bool anyStaged = selection.Any(r => r.Staged is not null);
        string[] extensions = [.. selection.Select(r => Path.GetExtension(r.FileName)).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        MainWindowViewModel? main = this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;

        List<Control> items =
        [
            MenuKit.Item(count == 1 ? "Discard changes…" : $"Discard {count} selected changes…", "ResetWorkingDirChanges", () => viewModel.BeginDiscard(selection), "Delete"),
            MenuKit.Item(count == 1 ? "Stash this file" : $"Stash {count} selected files", "Stash", () => _ = viewModel.StashSelectedCommand.ExecuteAsync(null)),
            MenuKit.Sep(),
            MenuKit.Item(count == 1 ? "Include in the commit (stage)" : "Include selected files (stage)", "Stage", () => _ = viewModel.IncludeSelectedAsync(), "Space",
                enabled: anyUnstaged, disabledTip: "Already included"),
            MenuKit.Item(count == 1 ? "Exclude from the commit (unstage)" : "Exclude selected files (unstage)", "Unstage", () => _ = viewModel.ExcludeSelectedAsync(),
                enabled: anyStaged, disabledTip: "Not included"),
            MenuKit.Sep(),
            MenuKit.Item($"Ignore {files} (add to .gitignore)", "AddToGitIgnore", () => _ = viewModel.AddToGitIgnoreAsync([.. selection.Select(r => "/" + r.Name)])),
        ];

        foreach (string extension in extensions)
        {
            items.Add(MenuKit.Item($"Ignore all {extension} files (add to .gitignore)", "AddToGitIgnore", () => _ = viewModel.AddToGitIgnoreAsync(["*" + extension])));
        }

        items.Add(MenuKit.Sep());
        items.AddRange(FileListContextMenu.Build(new FileMenuContext
        {
            Paths = [.. selection.Select(r => r.Name)],
            WorkingDir = viewModel.WorkingDir,
            Clipboard = new TopLevelClipboardService(TopLevel.GetTopLevel(this)),
            OpenHistory = main is null
                ? null
                : (path, blame) =>
                {
                    if (blame)
                    {
                        main.ShowFileBlameCommand.Execute(selection.FirstOrDefault(r => r.Name == path)?.Shown);
                    }
                    else
                    {
                        main.ShowFileHistoryWindowCommand.Execute(path);
                    }
                },
            Owner = TopLevel.GetTopLevel(this) as Window,
            Changed = viewModel.RefreshAsync,
        }));
        return items;
    }
}
