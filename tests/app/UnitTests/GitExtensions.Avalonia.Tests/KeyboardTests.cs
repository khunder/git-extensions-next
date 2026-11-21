using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;
using GitExtensions.Desktop.Views;

namespace GitExtensionsDesktopTests;

[TestFixture]
public class KeyboardTests
{
    private sealed class NoFolderPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    private ThemeService _previousTheme = null!;
    private string _themeFile = null!;

    [SetUp]
    public void IsolateTheme()
    {
        _previousTheme = ThemeService.Current;
        _themeFile = Path.Combine(Path.GetTempPath(), "ge-keys-theme-" + Guid.NewGuid().ToString("N") + ".json");
        ThemeService.Current = new ThemeService(new ThemeStore(_themeFile));
    }

    [TearDown]
    public void RestoreTheme()
    {
        ThemeService.Current = _previousTheme;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
        }

        File.Delete(_themeFile);
    }

    private static async Task<(MainWindow Window, MainWindowViewModel ViewModel, List<string> Invoked)> OpenAsync(TestRepository repository, double width = 1200, double height = 720)
    {
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new NoFolderPicker());
        MainWindow window = new() { DataContext = viewModel, Width = width, Height = height };
        window.Show();
        await viewModel.OpenAsync(repository.Path);
        Pump();
        List<string> invoked = [];
        viewModel.CommandInvoked += command => invoked.Add(command.Id);
        return (window, viewModel, invoked);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        window.KeyPress(key, (RawInputModifiers)(int)modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, (RawInputModifiers)(int)modifiers, PhysicalKey.None, null);
        Pump();
    }

    // A key that closes the dialog disposes its window: there is no release to send.
    private static void PressInDialog(Window dialog, Key key)
    {
        dialog.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Pump();
    }

    private static void Press(Window window, CommandGesture gesture) => Press(window, gesture.Key, gesture.Modifiers);

    // Commands such as refresh and fetch reload the repository in the background.
    private static async Task WaitIdleAsync(MainWindowViewModel viewModel)
    {
        for (int i = 0; i < 200 && (viewModel.IsBusy || viewModel.Remote.IsRunning || viewModel.Commits.Count != 2); i++)
        {
            await Task.Delay(25);
            Pump();
        }
    }

    // A ListBox itself is not focusable; its rows are.
    private static Control FocusFirstCommit(MainWindow window)
    {
        ListBox list = window.Find<ListBox>("CommitList");
        Control row = list.ContainerFromIndex(0)!;
        row.Focus().Should().BeTrue();
        Pump();
        return row;
    }

    private static void HideFlyouts(MainWindow window)
    {
        window.FindControl<MainToolbarView>("MainToolbar")!.RepoPath.Flyout?.Hide();
        window.FindControl<MainToolbarView>("MainToolbar")!.BranchList.Flyout?.Hide();
        foreach (StashMenuButton stash in window.GetVisualDescendants().OfType<StashMenuButton>())
        {
            stash.Flyout?.Hide();
        }

        Pump();
    }

    private static void CloseOverlays(MainWindow window, MainWindowViewModel viewModel)
    {
        for (int i = 0; i < 5 && viewModel.IsOverlayOpen; i++)
        {
            Press(window, Key.Escape);
        }
    }

    private static void SaveScreenshot(Window window, string name)
    {
        Pump();
        string directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, name + ".png");
        window.CaptureRenderedFrame()!.Save(file);
        TestContext.AddTestAttachment(file);
    }

    [AvaloniaTest]
    public void The_registry_has_unique_ids_and_unique_gestures()
    {
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new NoFolderPicker());
        IReadOnlyList<AppCommand> commands = viewModel.Commands;

        commands.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        commands.Select(c => c.Title).Should().OnlyHaveUniqueItems();
        commands.SelectMany(c => c.Gestures).Select(g => (g.Key, g.Modifiers)).Should().OnlyHaveUniqueItems();
        commands.Should().OnlyContain(c => c.Gestures.Count > 0 && !string.IsNullOrWhiteSpace(c.Title));

        // The documented shortcuts.
        string[] expected = ["repo.open", "repo.refresh", "remote.fetch", "remote.pull", "remote.push", "index.stageAll", "index.unstageAll", "commit.commit",
            "view.sidebar", "view.output", "focus.filter", "focus.path", "view.settings", "stash.show", "view.theme", "history.next", "history.previous",
            "file.history", "file.blame", "operation.continue", "operation.abort", "palette.show", "help.shortcuts", "repo.close"];
        commands.Select(c => c.Id).Should().Contain(expected);
    }

    [AvaloniaTest]
    public async Task Every_gesture_runs_its_command_when_enabled_and_is_ignored_when_disabled()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);
        viewModel.SelectedChangedFile = viewModel.ChangedFiles.First();
        Pump();

        foreach (AppCommand command in viewModel.Commands.Where(c => c.FocusScope is null))
        {
            foreach (CommandGesture gesture in command.Gestures)
            {
                // The window has no focused text box here, so even plain keys (J, K, ?) are active.
                invoked.Clear();
                await WaitIdleAsync(viewModel);
                FocusFirstCommit(window);
                bool enabled = command.CanExecute();
                Press(window, gesture);
                if (enabled)
                {
                    invoked.Should().Contain(command.Id, $"{gesture.Text} should run {command.Id}");
                }
                else
                {
                    invoked.Should().NotContain(command.Id, $"{command.Id} is disabled");
                }

                CloseOverlays(window, viewModel);
                viewModel.Palette.Close();
                HideFlyouts(window);

                // The simple mode is a persisted view option: switch back, or every later test would see the simple window.
                if (command.Id == "view.simpleMode")
                {
                    viewModel.IsSimpleMode.Should().BeTrue();
                    viewModel.ViewOptions.SimpleMode = false;
                    Pump();
                }

                // Closing the repository returns to the dashboard: open it again for the gestures that follow.
                if (command.Id == "repo.close")
                {
                    viewModel.HasOpenedRepository.Should().BeFalse();
                    await viewModel.OpenAsync(repository.Path);
                    viewModel.SelectedChangedFile = viewModel.ChangedFiles.First();
                    Pump();
                }
            }
        }

        window.Close();
    }

    [AvaloniaTest]
    public async Task Plain_letter_gestures_do_not_fire_while_typing_in_a_text_box()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);

        window.FindControl<RefsTreeView>("SidebarPanel")!.FindControl<TextBox>("FilterBox")!.Focus();
        Pump();
        Press(window, Key.J);
        Press(window, Key.K);
        Press(window, Key.OemQuestion, KeyModifiers.Shift);
        invoked.Should().BeEmpty();
        viewModel.ShortcutsHelp.IsOpen.Should().BeFalse();

        // Outside a text box the same keys work.
        FocusFirstCommit(window);
        Press(window, Key.J);
        invoked.Should().Equal("history.next");
        Press(window, Key.OemQuestion, KeyModifiers.Shift);
        viewModel.ShortcutsHelp.IsOpen.Should().BeTrue();
        window.Close();
    }

    [AvaloniaTest]
    public async Task Chords_still_work_in_a_text_box_but_never_the_standard_editing_keys()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);
        viewModel.Commands.SelectMany(c => c.Gestures).Should().NotContain(g =>
            g.Modifiers == KeyModifiers.Control && (g.Key == Key.A || g.Key == Key.C || g.Key == Key.V || g.Key == Key.X || g.Key == Key.Z || g.Key == Key.Y));

        window.FindControl<RefsTreeView>("SidebarPanel")!.FindControl<TextBox>("FilterBox")!.Focus();
        Pump();
        Press(window, Key.F5);
        invoked.Should().Equal("repo.refresh");
        window.Close();
    }

    [AvaloniaTest]
    public async Task J_and_K_walk_the_history_and_ctrl_enter_commits_only_from_the_commit_box()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);

        Press(window, Key.J);
        viewModel.SelectedCommit.Should().BeSameAs(viewModel.Commits[0]);
        Press(window, Key.J);
        viewModel.SelectedCommit.Should().BeSameAs(viewModel.Commits[1]);
        Press(window, Key.J);
        viewModel.SelectedCommit.Should().BeSameAs(viewModel.Commits[1], "the list ends here");
        Press(window, Key.K);
        viewModel.SelectedCommit.Should().BeSameAs(viewModel.Commits[0]);

        // Ctrl+Enter elsewhere does nothing; inside the box it commits (the file is staged first).
        invoked.Clear();
        Press(window, Key.Return, KeyModifiers.Control);
        invoked.Should().BeEmpty();

        // The commit box moved to the Commit window: the shortcut is scoped to that panel, so the main window ignores it...
        repository.RunGit("add", "a.txt");
        await viewModel.RefreshChangesAsync();
        viewModel.Commit!.Message = "From the keyboard";
        Press(window, Key.Return, KeyModifiers.Control);
        invoked.Should().BeEmpty();

        // ...and the registry command that the Commit window's box triggers still commits.
        AppCommand commit = viewModel.Commands.Single(c => c.Id == "commit.commit");
        commit.Gestures.Should().Contain(g => g.Key == Key.Return && g.Modifiers == KeyModifiers.Control);
        commit.FocusScope.Should().Be("CommitPanel");
        commit.CanExecute().Should().BeTrue();
        viewModel.Run(commit);
        invoked.Should().Equal("commit.commit");
        for (int i = 0; i < 100 && !repository.GitOutput("log", "--format=%s", "-1").Contains("From the keyboard"); i++)
        {
            await Task.Delay(50);
            Pump();
        }

        repository.GitOutput("log", "--format=%s", "-1").Should().Contain("From the keyboard");
        window.Close();
    }

    [AvaloniaTest]
    public async Task Focus_shortcuts_move_the_focus()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);

        // Ctrl+L goes to the repository path of the toolbar: its drop-down of recent repositories opens.
        Press(window, Key.L, KeyModifiers.Control);
        Pump();
        window.FindControl<MainToolbarView>("MainToolbar")!.RepoPath.Flyout!.IsOpen.Should().BeTrue();
        HideFlyouts(window);

        Press(window, Key.F, KeyModifiers.Control | KeyModifiers.Alt);
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(window.FindControl<RefsTreeView>("SidebarPanel")!.FindControl<TextBox>("FilterBox"));

        // Ctrl+F searches the commit messages: the revision filter gets the focus.
        Press(window, Key.F, KeyModifiers.Control);
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(window.FindControl<RevisionGridView>("RevisionGrid")!.FilterTextBox);

        // The commit message lives in the Commit window now: Ctrl+M asks for it.
        RecordingWindowService windows = new();
        viewModel.WindowService = windows;
        Press(window, Key.M, KeyModifiers.Control);
        windows.CommitWindows.Should().Be(1);

        Press(window, Key.B, KeyModifiers.Control);
        window.SidebarCollapsed.Should().BeTrue();
        Press(window, Key.B, KeyModifiers.Control);
        window.SidebarCollapsed.Should().BeFalse();

        Press(window, Key.O, KeyModifiers.Control | KeyModifiers.Shift);
        viewModel.Remote.IsOutputVisible.Should().BeTrue();
        window.Close();
    }

    [AvaloniaTest]
    public async Task Theme_settings_and_file_history_shortcuts_work()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);

        Press(window, Key.D, KeyModifiers.Control | KeyModifiers.Shift);
        ThemeService.Current.Choice.Should().Be(ThemeChoice.Light);
        Press(window, Key.D, KeyModifiers.Control | KeyModifiers.Shift);
        ThemeService.Current.Choice.Should().Be(ThemeChoice.Dark);

        Press(window, Key.OemComma, KeyModifiers.Control);
        viewModel.Settings.IsOpen.Should().BeTrue();
        Press(window, Key.Escape);
        viewModel.Settings.IsOpen.Should().BeFalse();

        // No changed file selected: Ctrl+H is disabled.
        viewModel.SelectedChangedFile = null;
        Press(window, Key.H, KeyModifiers.Control);
        viewModel.FileHistory.IsOpen.Should().BeFalse();

        viewModel.SelectedChangedFile = viewModel.ChangedFiles.First(f => f.Name == "a.txt");
        window.Find<ListBox>("CommitList").Focus();
        Press(window, Key.H, KeyModifiers.Control);
        viewModel.FileHistory.IsOpen.Should().BeTrue();
        viewModel.FileHistory.FilePath.Should().Be("a.txt");
        Press(window, Key.Escape);
        viewModel.FileHistory.IsOpen.Should().BeFalse();
        window.Close();
    }

    [AvaloniaTest]
    public async Task The_palette_opens_filters_navigates_and_runs()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);
        CommandPaletteView view = window.FindControl<CommandPaletteView>("PaletteOverlay")!;
        view.IsVisible.Should().BeFalse();

        Press(window, Key.P, KeyModifiers.Control | KeyModifiers.Shift);
        viewModel.Palette.IsOpen.Should().BeTrue();
        view.IsVisible.Should().BeTrue();
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(view.FindControl<TextBox>("FilterBox"));

        // Only the commands that can run now are listed (no merge/rebase is in progress), each with its gesture.
        viewModel.Palette.Items.Select(i => i.Command.Id).Should().NotContain(["operation.continue", "operation.abort", "palette.show"]);
        viewModel.Palette.Items.Should().OnlyContain(i => i.Gesture.Length > 0);

        window.KeyTextInput("fetch");
        Pump();
        viewModel.Palette.Filter.Should().Be("fetch");
        viewModel.Palette.Items.Select(i => i.Command.Id).Should().Contain("remote.fetch").And.NotContain("remote.pull");
        viewModel.Palette.Items[0].Command.Id.Should().Be("remote.fetch");

        // Down/Up move the selection (wrapping); Enter runs the selected command and closes the palette.
        viewModel.Palette.Filter = "p";
        Pump();
        viewModel.Palette.Items.Count.Should().BeGreaterThan(2);
        Press(window, Key.Down);
        viewModel.Palette.SelectedIndex.Should().Be(1);
        Press(window, Key.Up);
        Press(window, Key.Up);
        viewModel.Palette.SelectedIndex.Should().Be(viewModel.Palette.Items.Count - 1);

        viewModel.Palette.Filter = "toggle output";
        Pump();
        invoked.Clear();
        Press(window, Key.Enter);
        invoked.Should().Equal("view.output");
        viewModel.Palette.IsOpen.Should().BeFalse();
        view.IsVisible.Should().BeFalse();
        viewModel.Remote.IsOutputVisible.Should().BeTrue();

        // Ctrl+P also opens it; Esc closes it without running anything; the other shortcuts are inert while it is open.
        Press(window, Key.P, KeyModifiers.Control);
        viewModel.Palette.IsOpen.Should().BeTrue();
        invoked.Clear();
        Press(window, Key.F5);
        invoked.Should().BeEmpty();
        Press(window, Key.Escape);
        viewModel.Palette.IsOpen.Should().BeFalse();
        invoked.Should().BeEmpty();

        // A filter without matches shows the empty text and Enter does nothing.
        Press(window, Key.P, KeyModifiers.Control);
        invoked.Clear();
        viewModel.Palette.Filter = "zzzzqq";
        Pump();
        viewModel.Palette.Items.Should().BeEmpty();
        view.FindControl<TextBlock>("EmptyText")!.IsVisible.Should().BeTrue();
        Press(window, Key.Enter);
        invoked.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaTest]
    public async Task A_palette_command_that_focuses_wins_over_the_focus_restore()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);
        FocusFirstCommit(window);

        Press(window, Key.P, KeyModifiers.Control);
        viewModel.Palette.Filter = "repository path";
        Pump();
        Press(window, Key.Enter);
        Pump();
        window.FindControl<MainToolbarView>("MainToolbar")!.RepoPath.Flyout!.IsOpen.Should().BeTrue();
        HideFlyouts(window);
        window.Close();
    }

    [AvaloniaTest]
    public async Task The_help_overlay_lists_every_command_and_gesture()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);

        Press(window, Key.F1);
        viewModel.ShortcutsHelp.IsOpen.Should().BeTrue();
        Pump();
        DialogWindow dialog = DialogWindow.Open.Should().ContainSingle().Which;
        dialog.Title.Should().Be("Keyboard shortcuts");
        ShortcutsHelpView view = (ShortcutsHelpView)dialog.View;

        List<ShortcutEntry> entries = viewModel.ShortcutsHelp.Groups.SelectMany(g => g.Entries).ToList();
        entries.Should().HaveCount(viewModel.Commands.Count);
        foreach (AppCommand command in viewModel.Commands)
        {
            entries.Should().Contain(e => e.Title == command.Title && e.Gesture == command.GestureText);
        }

        viewModel.ShortcutsHelp.Groups.Select(g => g.Name).Should().OnlyHaveUniqueItems().And.Contain(["Repository", "Changes", "History", "View", "Navigation", "Help"]);

        // The rendered text contains them too.
        string[] texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();
        texts.Should().Contain("Ctrl+Shift+P, Ctrl+P").And.Contain("F1, ?");

        PressInDialog(dialog, Key.Escape);
        viewModel.ShortcutsHelp.IsOpen.Should().BeFalse();
        DialogWindow.Open.Should().BeEmpty();

        // The "?" key opens it too.
        Press(window, Key.OemQuestion, KeyModifiers.Shift);
        viewModel.ShortcutsHelp.IsOpen.Should().BeTrue();
        Pump();
        PressInDialog(DialogWindow.Open.Should().ContainSingle().Which, Key.Escape);
        viewModel.ShortcutsHelp.IsOpen.Should().BeFalse();
        Pump();
        DialogWindow.Open.Should().BeEmpty();
        window.Close();
    }

    [AvaloniaTest]
    public async Task Escape_closes_only_the_topmost_overlay_and_focus_returns()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);
        Control list = FocusFirstCommit(window);

        await viewModel.Settings.OpenAsync();
        Pump();
        viewModel.Settings.IsOpen.Should().BeTrue();
        DialogWindow settings = DialogWindow.Open.Should().ContainSingle().Which;

        // Help on top of the settings: Esc closes the help only.
        viewModel.ShortcutsHelp.Open();
        Pump();
        DialogWindow.Open.Should().HaveCount(2);
        PressInDialog(DialogWindow.Open[^1], Key.Escape);
        viewModel.ShortcutsHelp.IsOpen.Should().BeFalse();
        viewModel.Settings.IsOpen.Should().BeTrue();
        DialogWindow.Open.Should().ContainSingle().Which.Should().BeSameAs(settings);

        // Palette on top of the settings (an overlay of the main window).
        Press(window, Key.P, KeyModifiers.Control);
        viewModel.Palette.IsOpen.Should().BeTrue();
        Press(window, Key.Escape);
        viewModel.Palette.IsOpen.Should().BeFalse();
        viewModel.Settings.IsOpen.Should().BeTrue();

        PressInDialog(settings, Key.Escape);
        viewModel.Settings.IsOpen.Should().BeFalse();
        DialogWindow.Open.Should().BeEmpty();
        window.Activate();
        list.Focus();
        Pump();

        // File history: opened from the keyboard, closed by Esc, focus returns to the list.
        viewModel.SelectedChangedFile = viewModel.ChangedFiles.First();
        await viewModel.FileHistory.OpenAsync("a.txt", blame: false);
        Pump();
        Press(window, Key.Escape);
        viewModel.FileHistory.IsOpen.Should().BeFalse();
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(list);

        // The palette gives the focus back too.
        Press(window, Key.P, KeyModifiers.Control);
        Press(window, Key.Escape);
        window.FocusManager.GetFocusedElement().Should().BeSameAs(list);
        window.Close();
    }

    [AvaloniaTest]
    public async Task Escape_cancels_the_credential_prompt_and_shortcuts_are_inert_behind_it()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, List<string> invoked) = await OpenAsync(repository);

        Task<AskPassResponse> pending = viewModel.Credentials.RequestAsync(
            AskPassPromptClassifier.Classify("Password for 'https://example.com': "),
            CancellationToken.None);
        Pump();
        viewModel.Credentials.IsOpen.Should().BeTrue();
        DialogWindow dialog = DialogWindow.Open.Should().ContainSingle().Which;

        Press(window, Key.F5);
        Press(window, Key.OemComma, KeyModifiers.Control);
        invoked.Should().BeEmpty();
        viewModel.Settings.IsOpen.Should().BeFalse();

        PressInDialog(dialog, Key.Escape);
        viewModel.Credentials.IsOpen.Should().BeFalse();
        DialogWindow.Open.Should().BeEmpty();
        (await pending).Reply.Should().BeNull();
        window.Close();
    }

    [AvaloniaTest]
    public async Task Icon_only_buttons_and_main_lists_have_automation_names()
    {
        using TestRepository repository = new();
        (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository);
        viewModel.Remote.IsOutputVisible = true;
        Pump();

        List<string> offenders = [];
        foreach (Button button in window.GetVisualDescendants().OfType<Button>())
        {
            bool templatePart = button.Name?.StartsWith("PART_", StringComparison.Ordinal) ?? false;
            bool iconOnly = !templatePart && (button.Content is not string text || !text.Any(char.IsLetterOrDigit));
            if (iconOnly && string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)))
            {
                offenders.Add(button.Name ?? button.GetType().Name);
            }
        }

        offenders.Should().BeEmpty();

        foreach (string name in new[] { "RepoPathButton", "BranchButton", "PullButton", "PushButton", "CommitButton", "RefreshButton", "SettingsButton", "CommitList", "FilterBox", "RefsTree", "FileHistoryOverlay", "PaletteOverlay" })
        {
            Control? control = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
            control.Should().NotBeNull(name);
            Avalonia.Automation.AutomationProperties.GetName(control!).Should().NotBeNullOrWhiteSpace(name);
        }

        // History rows announce subject, author, date and hash.
        ListBoxItem row = window.Find<ListBox>("CommitList").GetVisualDescendants().OfType<ListBoxItem>().First();
        Avalonia.Automation.AutomationProperties.GetName(row).Should().Contain("Add b.txt").And.Contain("by ");
        window.Close();
    }

    [AvaloniaTest]
    public async Task The_history_row_compacts_as_the_window_narrows()
    {
        using TestRepository repository = new();
        // The grid takes the whole width right of the left panel; a forced width stands in for a very narrow window.
        foreach ((double width, double height, double forced, string state) in new[] { (800.0, 560.0, 0.0, "medium"), (900.0, 600.0, 0.0, "wide"), (900.0, 600.0, 420.0, "narrow"), (900.0, 600.0, 340.0, "narrow"), (1800.0, 900.0, 0.0, "wide") })
        {
            (MainWindow window, _, _) = await OpenAsync(repository, width, height);
            if (forced > 0)
            {
                window.Find<RevisionGridView>("RevisionGrid").Width = forced;
                Pump();
            }

            ListBox list = window.Find<ListBox>("CommitList");
            TextBlock subject = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Add b.txt");
            TextBlock author = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("author"));
            TextBlock date = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("date"));

            window.HistoryLayoutState.Should().Be(state, $"{width}x{height}: the list is {list.Bounds.Width:0}px wide");
            subject.Bounds.Width.Should().BeGreaterThan(100, $"{width}x{height}: the list is {list.Bounds.Width:0}px wide");
            list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ref") && b.IsVisible).All(b => (b.Bounds.Width > 0) == !list.Classes.Contains("tiny")).Should().BeTrue("ref badges collapse in the tiny state");
            author.IsEffectivelyVisible.Should().Be(state != "narrow");
            date.IsEffectivelyVisible.Should().Be(state == "wide");
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Small_windows_keep_the_panes_and_bars_usable()
    {
        using TestRepository repository = new();
        foreach ((double width, double height) in new[] { (900.0, 600.0), (800.0, 560.0) })
        {
            (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository, width, height);
            string size = $"{width}x{height}";

            window.Find<RevisionGridView>("RevisionGrid").Bounds.Width.Should().BeGreaterThanOrEqualTo(280, size);
            window.Find<RevisionGridView>("RevisionGrid").Bounds.Height.Should().BeGreaterThanOrEqualTo(120, size);
            window.Find<RevisionTabsView>("RevisionTabs").Bounds.Height.Should().BeGreaterThanOrEqualTo(120, size);

            // The bars fit the window: every visible control of the repository and remote bars ends inside it.
            foreach (string bar in new[] { "RefreshButton", "RepoPathButton", "BranchButton", "PullButton", "PushButton", "CommitButton", "FileExplorerButton", "ShellButton", "SettingsButton" })
            {
                Control control = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == bar);
                Point right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value;
                right.X.Should().BeLessThanOrEqualTo(window.ClientSize.Width, $"{bar} at {size}");
            }

            // Every pane ends inside the window.
            foreach (string pane in new[] { "SidebarPanel", "RevisionGrid", "RevisionTabs", "CommitList" })
            {
                Control control = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == pane);
                control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X.Should().BeLessThanOrEqualTo(window.ClientSize.Width + 0.5, $"{pane} at {size}");
            }

            window.FindControl<Control>("MainToolbar")!.Bounds.Right.Should().BeLessThanOrEqualTo(window.ClientSize.Width, size);
            viewModel.Remote.HasRepository.Should().BeTrue();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Screenshots_of_the_palette_help_and_small_windows_in_light_and_dark()
    {
        using TestRepository repository = new();
        repository.AddBranchesRemoteAndTags();
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = variant;
            string suffix = variant.Key.ToString()!.ToLowerInvariant();

            (MainWindow window, MainWindowViewModel viewModel, _) = await OpenAsync(repository, 900, 600);
            SaveScreenshot(window, "window-900x600-" + suffix);

            Press(window, Key.P, KeyModifiers.Control);
            viewModel.Palette.Filter = "s";
            SaveScreenshot(window, "palette-" + suffix);
            Press(window, Key.Escape);

            Press(window, Key.F1);
            SaveScreenshot(window, "shortcuts-help-" + suffix);
            Press(window, Key.Escape);
            window.Close();

            (MainWindow small, _, _) = await OpenAsync(repository, 800, 560);
            SaveScreenshot(small, "window-800x560-" + suffix);

            // The visible focus ring on a keyboard-focused button.
            small.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PushButton").Focus(NavigationMethod.Tab);
            SaveScreenshot(small, "focus-ring-" + suffix);
            small.Close();
        }
    }
}
