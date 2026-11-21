using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Headless.NUnit;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;
using GitExtensions.Desktop.Views;

namespace GitExtensionsDesktopTests;

[TestFixture]
public class SimpleModeTests
{
    private sealed class FakeFolderPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    private static MainWindowViewModel CreateViewModel() => new(GitEnvironment.Initialize(), new FakeFolderPicker());

    private static void Commit(TestRepository repository, string file, string message)
    {
        File.WriteAllText(Path.Combine(repository.Path, file), message);
        repository.RunGit("add", file);
        repository.RunGit("-c", "user.name=Test Author", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", message);
    }

    private static bool IsAncestor(TestRepository repository, string ancestor, string of)
    {
        ProcessStartInfo startInfo = new("git") { WorkingDirectory = repository.Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "merge-base", "--is-ancestor", ancestor, of })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            watch.ElapsedMilliseconds.Should().BeLessThan(timeoutMs, "the condition should become true");
            await Task.Delay(20);
        }
    }

    [AvaloniaTest]
    public void Simple_mode_shows_the_changes_tab_and_hides_the_full_history()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            viewModel.ToggleSimpleModeCommand.Execute(null);

            viewModel.IsSimpleMode.Should().BeTrue();
            viewModel.IsChangesTabEnabled.Should().BeTrue();
            viewModel.IsChangesTabSelected.Should().BeTrue();
            viewModel.IsFullHistoryVisible.Should().BeFalse();

            viewModel.SidebarTabIndex = 1;
            viewModel.IsSimpleHistoryVisible.Should().BeTrue();
            viewModel.IsFullHistoryVisible.Should().BeFalse();
        }
        finally
        {
            viewModel.ViewOptions.SimpleMode = false;
        }

        viewModel.IsFullMode.Should().BeTrue();
    }

    [AvaloniaTest]
    public void The_graph_view_adds_branch_lines_to_the_same_simple_list()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            viewModel.ViewOptions.SimpleMode = true;
            viewModel.SidebarTabIndex = 1;
            viewModel.ShowHistoryViewSwitch.Should().BeTrue();
            viewModel.IsHistoryListView.Should().BeTrue();

            viewModel.IsHistoryGraphView = true;

            viewModel.IsSimpleHistoryVisible.Should().BeTrue("the simple layout stays; only the list draws the branch lines");
            viewModel.IsFullHistoryVisible.Should().BeFalse();
            viewModel.IsSimpleMode.Should().BeTrue("the toolbar and menus stay simple");
            viewModel.ViewOptions.Settings.SimpleHistoryGraph.Should().BeTrue("the choice is remembered");

            viewModel.IsHistoryListView = true;
            viewModel.IsSimpleHistoryVisible.Should().BeTrue();
        }
        finally
        {
            viewModel.ViewOptions.SimpleHistoryGraph = false;
            viewModel.ViewOptions.SimpleMode = false;
        }

        viewModel.ShowHistoryViewSwitch.Should().BeFalse();
    }

    [AvaloniaTest]
    public async Task Update_from_main_merges_the_main_branch_into_the_current_one()
    {
        using TestRepository repository = new();
        repository.RunGit("checkout", "--quiet", "-b", "topic");
        repository.RunGit("checkout", "--quiet", "main");
        Commit(repository, "main-only.txt", "Work on main");
        repository.RunGit("checkout", "--quiet", "topic");

        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.OpenAsync(repository.Path);
        await WaitUntilAsync(() => viewModel.DefaultBranch == "main");

        viewModel.CanUpdateFromDefaultBranch.Should().BeTrue();
        viewModel.UpdateFromDefaultBranchText.Should().Be("Update from main");

        await viewModel.UpdateFromDefaultBranchCommand.ExecuteAsync(null);

        IsAncestor(repository, "main", "topic").Should().BeTrue();
    }

    [AvaloniaTest]
    public async Task On_the_main_branch_there_is_nothing_to_update_from()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.OpenAsync(repository.Path);
        await WaitUntilAsync(() => viewModel.DefaultBranch == "main");

        viewModel.CanUpdateFromDefaultBranch.Should().BeFalse();
    }

    [AvaloniaTest]
    public async Task The_branch_picker_switches_branches_or_merges_one_in()
    {
        using TestRepository repository = new();
        repository.RunGit("checkout", "--quiet", "-b", "feature");
        Commit(repository, "feature.txt", "Feature work");
        repository.RunGit("checkout", "--quiet", "main");

        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.OpenAsync(repository.Path);
        await WaitUntilAsync(() => viewModel.PickerBranches.Count == 2);

        viewModel.StartMergePickerCommand.Execute(null);
        viewModel.PickerBranches.Select(item => item.Name).Should().Equal("feature");
        viewModel.BranchPickerTitle.Should().Be("Choose a branch to merge into main");

        await viewModel.PickBranchCommand.ExecuteAsync("feature");

        IsAncestor(repository, "feature", "main").Should().BeTrue();
        viewModel.IsMergePicker.Should().BeFalse();

        repository.RunGit("branch", "other");
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.PickBranchCommand.ExecuteAsync("other");
        await WaitUntilAsync(() => viewModel.Branch == "other");
    }

    [AvaloniaTest]
    public async Task All_files_browses_the_folders_of_the_selected_commit_and_stays_chosen()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = CreateViewModel();
        await viewModel.OpenAsync(repository.Path);
        viewModel.IsShowingChangedFiles.Should().BeTrue();

        viewModel.SelectedCommit = viewModel.Commits[^1];
        viewModel.IsBrowsingAllFiles = true;

        // The first commit only has a.txt; the tree loads now that it is shown.
        await WaitUntilAsync(() => viewModel.RevisionTabs.FileTree.Roots.Any(node => node.Name == "a.txt"));
        viewModel.RevisionTabs.FileTree.Roots.Select(node => node.Name).Should().NotContain("b.txt");
        viewModel.IsShowingChangedFiles.Should().BeFalse();

        viewModel.SelectedCommit = viewModel.Commits[0];
        await WaitUntilAsync(() => viewModel.RevisionTabs.FileTree.Roots.Any(node => node.Name == "b.txt"));
        viewModel.IsBrowsingAllFiles.Should().BeTrue();

        viewModel.IsShowingChangedFiles = true;
        viewModel.RevisionTabs.SelectedTabIndex.Should().Be(RevisionTabsViewModel.DiffTab);
    }

    [AvaloniaTest]
    public async Task The_repository_list_shows_recent_ones_then_groups_by_GitHub_owner_and_filters()
    {
        using TestRepository current = new();
        using TestRepository onGitHub = new();
        using TestRepository local = new();
        onGitHub.RunGit("remote", "add", "origin", "https://github.com/acme/widgets.git");
        FakeHistory history = new();
        history.Recent.AddRange([new(current.Path), new(onGitHub.Path), new(local.Path)]);

        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new FakeFolderPicker(), history);
        await viewModel.OpenAsync(current.Path);
        await viewModel.LoadRepositoryListAsync();

        viewModel.RepositoryGroups.Select(group => group.Title).Should().Equal("Recent", "acme", "Other");
        viewModel.RepositoryGroups[0].Items.Select(item => item.Path).Should().Equal(onGitHub.Path, local.Path);
        viewModel.RepositoryGroups[1].Items.Single().Path.Should().Be(onGitHub.Path);
        viewModel.RepositoryGroups[2].Items.Should().ContainSingle(item => item.IsCurrent);

        viewModel.RepositoryFilter = Path.GetFileName(local.Path);
        viewModel.RepositoryGroups.Select(group => group.Title).Should().Equal("Other");
        viewModel.RepositoryFilter = "no such repository";
        viewModel.HasNoRepositoryMatches.Should().BeTrue();

        await viewModel.OpenRepositoryFromListCommand.ExecuteAsync(new RepositoryListItem("widgets", onGitHub.Path, false));
        Path.TrimEndingDirectorySeparator(viewModel.RepositoryPath).Should().Be(onGitHub.Path);
        viewModel.RepositoryFilter.Should().BeEmpty();
    }

    [AvaloniaTest]
    public async Task Clicking_a_repository_in_the_drop_down_opens_it()
    {
        using TestRepository current = new();
        using TestRepository other = new();
        FakeHistory history = new();
        history.Recent.AddRange([new(current.Path), new(other.Path)]);
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new FakeFolderPicker(), history);
        viewModel.ViewOptions.SimpleMode = true;
        MainWindow window = new() { DataContext = viewModel, Width = 1100, Height = 700 };
        try
        {
            window.Show();
            await viewModel.OpenAsync(current.Path);
            Button repositoryButton = window.Find<Button>("SimpleRepositoryButton");
            repositoryButton.Flyout!.ShowAt(repositoryButton);
            await WaitUntilAsync(() => viewModel.RepositoryGroups.Count > 0);
            ShellTestSupport.Settle();

            // The rows live in the drop-down's popup: find the one of the other repository there.
            Button row = repositoryButton.Flyout is Flyout { Content: Control content }
                ? content.GetVisualDescendants().OfType<Button>().First(b => b.CommandParameter is RepositoryListItem item && item.Path == other.Path)
                : throw new InvalidOperationException("No drop-down content.");
            row.Command.Should().NotBeNull("the row must reach the window's view-model from inside the popup");

            // What Button.OnClick does: Click first (which closes the drop-down), then the command.
            row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            row.Command.Should().NotBeNull("closing the drop-down must not clear the command before the button runs it");
            row.Command!.Execute(row.CommandParameter);

            await WaitUntilAsync(() => Path.TrimEndingDirectorySeparator(viewModel.RepositoryPath) == other.Path && !viewModel.IsBusy);
        }
        finally
        {
            viewModel.ViewOptions.SimpleMode = false;
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task The_simple_history_has_the_commit_menu_of_the_full_grid_and_the_graph_toggle()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = CreateViewModel();
        viewModel.ViewOptions.SimpleMode = true;
        MainWindow window = new() { DataContext = viewModel, Width = 1100, Height = 700 };
        try
        {
            window.Show();
            await viewModel.OpenAsync(repository.Path);
            viewModel.SidebarTabIndex = 1;
            ShellTestSupport.Settle();

            ListBox list = window.Find<ListBox>("SimpleHistoryList");
            list.IsEffectivelyVisible.Should().BeTrue();
            list.ContextMenu.Should().NotBeNull("right click offers the commit menu");
            ListBoxItem row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            row.RaiseEvent(new ContextRequestedEventArgs());
            list.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header is TextBlock text ? text.Text : item.Header?.ToString())
                .Should().Contain("Create new branch here…");

            list.Classes.Should().NotContain("graph");
            viewModel.IsHistoryGraphView = true;
            ShellTestSupport.Settle();
            list.Classes.Should().Contain("graph");
            row.GetVisualDescendants().OfType<CommitGraphCell>().Single().IsVisible.Should().BeTrue();
        }
        finally
        {
            viewModel.ViewOptions.SimpleHistoryGraph = false;
            viewModel.ViewOptions.SimpleMode = false;
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Commits_not_on_the_remote_yet_are_marked_and_counted()
    {
        using TestRepository repository = new();
        string remote = Path.Combine(Path.GetTempPath(), "ge-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remote);
        try
        {
            ProcessStartInfo init = new("git", ["init", "--quiet", "--bare", remote]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(init)!)
            {
                process.WaitForExit();
            }

            repository.RunGit("remote", "add", "origin", remote);
            repository.RunGit("push", "--quiet", "-u", "origin", "main");
            Commit(repository, "local1.txt", "First local");
            Commit(repository, "local2.txt", "Second local");

            MainWindowViewModel viewModel = CreateViewModel();
            await viewModel.OpenAsync(repository.Path);
            await WaitUntilAsync(() => viewModel.Commits.Count(commit => commit.IsUnpushed) == 2);

            viewModel.Commits.Where(commit => commit.IsUnpushed).Select(commit => commit.Subject).Should().Equal("Second local", "First local");
            viewModel.Commits.Single(commit => commit.Subject == "Add b.txt").IsUnpushed.Should().BeFalse();
            await WaitUntilAsync(() => viewModel.AheadCount == 2 && viewModel.HasAheadBadge);

            // The history reloads in bulk (a Reset, not item-by-item adds): the new rows must be marked too.
            await viewModel.ReloadHistoryAsync();
            viewModel.Commits.Count(commit => commit.IsUnpushed).Should().Be(2);

            // After pushing, nothing is marked any more.
            repository.RunGit("push", "--quiet");
            await viewModel.UpdateUnpushedAsync();
            viewModel.Commits.Should().OnlyContain(commit => !commit.IsUnpushed);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(remote, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task The_sync_button_follows_the_state_of_the_branch()
    {
        using TestRepository repository = new();
        string remote = Path.Combine(Path.GetTempPath(), "ge-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remote);
        try
        {
            ProcessStartInfo init = new("git", ["init", "--quiet", "--bare", remote]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(init)!)
            {
                process.WaitForExit();
            }

            MainWindowViewModel viewModel = CreateViewModel();
            await viewModel.OpenAsync(repository.Path);
            await WaitUntilAsync(() => viewModel.Remote.CurrentBranch == "main");
            viewModel.SyncAction.Should().Be(SyncAction.None);
            viewModel.SyncTitle.Should().Be("No remote");

            repository.RunGit("remote", "add", "origin", remote);
            await viewModel.RefreshCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.SyncAction == SyncAction.Publish);
            viewModel.SyncTitle.Should().Be("Publish branch");

            await viewModel.SyncCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.SyncAction == SyncAction.Fetch && !viewModel.IsBusy);
            viewModel.SyncTitle.Should().Be("Fetch origin");

            Commit(repository, "local.txt", "Not pushed yet");
            await viewModel.RefreshCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.SyncAction == SyncAction.Push);
            viewModel.SyncTitle.Should().Be("Push origin");
            viewModel.SyncDetail.Should().Be("1 commit to send");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(remote, recursive: true);
        }
    }
}
