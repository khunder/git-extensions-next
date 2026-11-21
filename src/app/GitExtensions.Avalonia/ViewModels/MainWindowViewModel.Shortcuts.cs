using Avalonia.Input;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>Window-level places the registry commands can ask the view to focus or toggle.</summary>
public enum UiTarget
{
    PathBox,
    SidebarFilter,
    RevisionFilter,
    CommitMessage,
    ToggleSidebar,
    ShowRecent,
    ShowStash,
    ShowTools,
    ShowSidebar,
    ShowBranches,
}

// The command registry, the command palette and the shortcuts help (kept out of the main file).
public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<AppCommand>? _commandList;
    private CommandPaletteViewModel? _palette;
    private ShortcutsHelpViewModel? _shortcutsHelp;

    /// <summary>Raised when a command needs the view (focus a box, toggle the sidebar, open a flyout).</summary>
    public event Action<UiTarget>? UiRequested;

    public CommandPaletteViewModel Palette => _palette ??= new CommandPaletteViewModel(() => Commands, Run);

    public ShortcutsHelpViewModel ShortcutsHelp => _shortcutsHelp ??= new ShortcutsHelpViewModel(() => Commands);

    /// <summary>The single list of commands behind the shortcuts, the palette and the help.</summary>
    public IReadOnlyList<AppCommand> Commands => _commandList ??= BuildCommands();

    /// <summary>True while a modal overlay (settings, file history, palette, help, credentials) has the window's input.</summary>
    public bool IsOverlayOpen => Settings.IsOpen || FileHistory.IsOpen || Palette.IsOpen || ShortcutsHelp.IsOpen || Credentials.IsOpen;

    /// <summary>Raised just before a registry command runs from a shortcut or the palette.</summary>
    public event Action<AppCommand>? CommandInvoked;

    /// <summary>Runs a registry command (the caller checked <see cref="AppCommand.CanExecute"/>).</summary>
    public void Run(AppCommand command)
    {
        CommandInvoked?.Invoke(command);
        command.Execute();
    }

    /// <summary>Selects the next (older) commit in the history list.</summary>
    public void SelectNextCommit() => StepCommit(1);

    /// <summary>Selects the previous (newer) commit in the history list.</summary>
    public void SelectPreviousCommit() => StepCommit(-1);

    private void StepCommit(int delta)
    {
        if (Commits.Count == 0)
        {
            return;
        }

        int index = SelectedCommit is null ? (delta > 0 ? -1 : Commits.Count) : Commits.IndexOf(SelectedCommit);
        SelectedCommit = Commits[Math.Clamp(index + delta, 0, Commits.Count - 1)];
    }

    // The Diff tab, while it is the selected tab and shows the uncommitted changes (the keys S and R act on its selected file).
    private RevisionDiffViewModel? WorkingTreeDiffTab
        => IsWorkingTreeSelected && _revisionTabs is { SelectedTabIndex: RevisionTabsViewModel.DiffTab, Diff: { HasWorkingTreeFile: true } diff } ? diff : null;

    // The palette and the help never show at the same time.
    private void TogglePalette()
    {
        ShortcutsHelp.Close();
        Palette.Toggle();
    }

    private void ToggleHelp()
    {
        Palette.Close();
        ShortcutsHelp.Toggle();
    }

    private static void CycleTheme()
    {
        // System, Light, Dark, then the named themes, and round again.
        ThemeChoice[] all = Enum.GetValues<ThemeChoice>();
        ThemeService.Current.Set(all[(Array.IndexOf(all, ThemeService.Current.Choice) + 1) % all.Length]);
    }

    private IReadOnlyList<AppCommand> BuildCommands()
    {
        const KeyModifiers Ctrl = KeyModifiers.Control;
        const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift;

        static IReadOnlyList<CommandGesture> Keys(params CommandGesture[] gestures) => gestures;

        bool HasRepo() => HasOpenedRepository;
        bool Idle() => HasOpenedRepository && !IsBusy;

        AppCommand Make(string id, string title, string group, Action execute, Func<bool>? can = null, IReadOnlyList<CommandGesture>? gestures = null, string? scope = null, bool palette = true)
            => new(id, title, group, execute, can ?? (() => true), gestures ?? [], scope, palette);

        AppCommand FromCommand(string id, string title, string group, Func<System.Windows.Input.ICommand?> command, Func<bool>? extra = null, IReadOnlyList<CommandGesture>? gestures = null, string? scope = null)
            => Make(
                id,
                title,
                group,
                () =>
                {
                    if (command() is { } c && c.CanExecute(null))
                    {
                        c.Execute(null);
                    }
                },
                () => (extra?.Invoke() ?? true) && command() is { } c && c.CanExecute(null),
                gestures,
                scope);

        return
        [
            FromCommand("repo.open", "Open repository…", "Repository", () => BrowseCommand, gestures: Keys(CommandGesture.Of(Key.O, Ctrl))),
            Make("repo.recent", "Show recent repositories", "Repository", () => UiRequested?.Invoke(UiTarget.ShowRecent), gestures: Keys(CommandGesture.Of(Key.R, Ctrl))),
            FromCommand("repo.close", "Close repository (go to the dashboard)", "Repository", () => CloseRepositoryCommand, HasRepo, Keys(CommandGesture.Of(Key.W, Ctrl))),
            Make("repo.refresh", "Refresh", "Repository", () => _ = OpenAsync(RepositoryPath), Idle, Keys(CommandGesture.Of(Key.F5))),
            FromCommand("remote.fetch", "Fetch", "Repository", () => Remote.FetchCommand, HasRepo, Keys(CommandGesture.Of(Key.F, CtrlShift))),
            FromCommand("remote.pull", "Pull…", "Repository", () => ShowPullDialogCommand, HasRepo, Keys(CommandGesture.Of(Key.L, CtrlShift))),
            FromCommand("remote.push", "Push…", "Repository", () => ShowPushDialogCommand, HasRepo, Keys(CommandGesture.Of(Key.K, CtrlShift))),
            Make("window.commit", "Commit…", "Repository", () => ShowCommitWindowCommand.Execute(null), HasRepo, Keys(CommandGesture.Of(Key.K, Ctrl))),
            Make("stash.show", "Manage stashes…", "Repository", () => Stash.Open(), HasRepo, Keys(CommandGesture.Of(Key.T, CtrlShift))),

            // Changes
            FromCommand("index.stageAll", "Stage all changes", "Changes", () => Diff?.StageAllCommand, HasRepo, Keys(CommandGesture.Of(Key.S, CtrlShift))),
            FromCommand("index.unstageAll", "Unstage all changes", "Changes", () => Diff?.UnstageAllCommand, HasRepo, Keys(CommandGesture.Of(Key.U, CtrlShift))),
            FromCommand("diff.stageFile", "Stage the selected file of the Diff tab (uncommitted changes)", "Changes", () => WorkingTreeDiffTab?.StageSelectedFileCommand, HasRepo, Keys(CommandGesture.Of(Key.S))),
            FromCommand("diff.revertFile", "Revert the selected file of the Diff tab (uncommitted changes)…", "Changes", () => WorkingTreeDiffTab?.RevertSelectedFileCommand, HasRepo, Keys(CommandGesture.Of(Key.R))),
            FromCommand("commit.commit", "Commit", "Changes", () => Commit?.CommitCommand, HasRepo, Keys(CommandGesture.Of(Key.Return, Ctrl)), scope: "CommitPanel"),
            Make("commit.focusMessage", "Focus commit message", "Changes", () => UiRequested?.Invoke(UiTarget.CommitMessage), HasRepo, Keys(CommandGesture.Of(Key.M, Ctrl))),
            FromCommand("file.history", "File history of the selected file", "Changes", () => ShowFileHistoryCommand.CanExecute(SelectedChangedFile) ? new BoundCommand(ShowFileHistoryCommand, SelectedChangedFile) : null, gestures: Keys(CommandGesture.Of(Key.H, Ctrl))),
            FromCommand("file.blame", "Blame the selected file", "Changes", () => ShowFileBlameCommand.CanExecute(SelectedChangedFile) ? new BoundCommand(ShowFileBlameCommand, SelectedChangedFile) : null, gestures: Keys(CommandGesture.Of(Key.B, CtrlShift))),

            // Operations in progress
            FromCommand("operation.continue", "Continue the merge or rebase", "Operations", () => Operation.IsActive ? Operation.ContinueCommand : null, gestures: Keys(CommandGesture.Of(Key.Return, CtrlShift))),
            FromCommand("operation.abort", "Abort the merge or rebase…", "Operations", () => Operation.IsActive ? Operation.RequestAbortCommand : null, gestures: Keys(CommandGesture.Of(Key.Back, CtrlShift))),

            // History
            Make("history.next", "Select next commit", "History", SelectNextCommit, () => Commits.Count > 0, Keys(CommandGesture.Of(Key.J))),
            Make("history.previous", "Select previous commit", "History", SelectPreviousCommit, () => Commits.Count > 0, Keys(CommandGesture.Of(Key.K))),

            Make("history.goToCommit", "Go to commit…", "History", () => ShowGoToCommitCommand.Execute(null), HasRepo, Keys(CommandGesture.Of(Key.G, CtrlShift))),
            FromCommand("history.back", "Navigate backward", "History", () => NavigateBackCommand, HasRepo, Keys(CommandGesture.Of(Key.Left, KeyModifiers.Alt))),
            FromCommand("history.forward", "Navigate forward", "History", () => NavigateForwardCommand, HasRepo, Keys(CommandGesture.Of(Key.Right, KeyModifiers.Alt))),

            // View
            Make("view.sidebar", "Toggle branches sidebar", "View", () => UiRequested?.Invoke(UiTarget.ToggleSidebar), HasRepo, Keys(CommandGesture.Of(Key.B, Ctrl))),
            Make("view.changesTab", "Show the Changes tab", "View", () => SelectSidebarTab(ChangesTabIndex), () => HasRepo() && IsChangesTabEnabled, Keys(CommandGesture.Of(Key.D1, Ctrl))),
            FromCommand("view.simpleMode", "Switch between simple and full mode", "View", () => ToggleSimpleModeCommand, gestures: Keys(CommandGesture.Of(Key.M, CtrlShift))),
            Make("view.historyTab", "Show the History tab", "View", () => SelectSidebarTab(HistoryTabIndex), () => HasRepo() && IsChangesTabEnabled, Keys(CommandGesture.Of(Key.D2, Ctrl))),
            FromCommand("view.output", "Toggle Output panel", "View", () => Remote.ToggleOutputCommand, HasRepo, Keys(CommandGesture.Of(Key.O, CtrlShift))),
            Make("view.theme", "Cycle theme (system, light, dark, graphite, mist, dusk, sand)", "View", CycleTheme, gestures: Keys(CommandGesture.Of(Key.D, CtrlShift))),
            Make("view.settings", "Settings", "View", () => _ = Settings.OpenAsync(), gestures: Keys(CommandGesture.Of(Key.OemComma, Ctrl))),

            // Navigation
            Make("focus.revisionfilter", "Search commit messages", "Navigation", () => UiRequested?.Invoke(UiTarget.RevisionFilter), HasRepo, Keys(CommandGesture.Of(Key.F, Ctrl))),
            Make("focus.filter", "Filter branches and tags", "Navigation", () => UiRequested?.Invoke(UiTarget.SidebarFilter), HasRepo, Keys(CommandGesture.Of(Key.F, KeyModifiers.Control | KeyModifiers.Alt))),
            Make("focus.path", "Focus the repository path", "Navigation", () => UiRequested?.Invoke(UiTarget.PathBox), gestures: Keys(CommandGesture.Of(Key.L, Ctrl))),

            // Help
            Make("palette.show", "Command palette", "Help", TogglePalette, gestures: Keys(CommandGesture.Of(Key.P, CtrlShift), CommandGesture.Of(Key.P, Ctrl)), palette: false),
            Make("help.shortcuts", "Keyboard shortcuts", "Help", ToggleHelp, gestures: Keys(CommandGesture.Of(Key.F1), CommandGesture.Of(Key.OemQuestion, KeyModifiers.Shift, "?"))),
        ];
    }

    // A command with a fixed parameter, adapted to ICommand for the registry.
    private sealed class BoundCommand(CommunityToolkit.Mvvm.Input.IRelayCommand inner, object? parameter) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? commandParameter) => inner.CanExecute(parameter);

        public void Execute(object? commandParameter) => inner.Execute(parameter);
    }
}
