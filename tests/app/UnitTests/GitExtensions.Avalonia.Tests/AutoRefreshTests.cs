using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.NUnit;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;

namespace GitExtensionsDesktopTests;

[TestFixture]
public class AutoRefreshTests
{
    private sealed class FakeFolderPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    private static RepositoryChanges Classify(string path, out string? workTreePath, params string[] gitDirs)
        => RepositoryWatcher.Classify(path, Root, gitDirs.Length == 0 ? [P(".git")] : gitDirs, out workTreePath);

    [Test]
    public void Classify_working_tree_file_is_a_work_tree_change_with_its_relative_path()
    {
        Classify(P("src", "a.txt"), out string? path).Should().Be(RepositoryChanges.WorkTree);
        path.Should().Be("src/a.txt");
    }

    [TestCase("index", RepositoryChanges.WorkTree)]
    [TestCase("info/exclude", RepositoryChanges.WorkTree)]
    [TestCase("HEAD", RepositoryChanges.All)]
    [TestCase("MERGE_HEAD", RepositoryChanges.All)]
    [TestCase("rebase-merge/done", RepositoryChanges.All)]
    [TestCase("refs/heads/main", RepositoryChanges.Refs)]
    [TestCase("refs/remotes/origin/main", RepositoryChanges.Refs)]
    [TestCase("packed-refs", RepositoryChanges.Refs)]
    [TestCase("config", RepositoryChanges.Refs)]
    [TestCase("index.lock", RepositoryChanges.None)]
    [TestCase("refs/heads/main.lock", RepositoryChanges.None)]
    [TestCase("objects/ab/cdef", RepositoryChanges.None)]
    [TestCase("logs/HEAD", RepositoryChanges.None)]
    [TestCase("FETCH_HEAD", RepositoryChanges.None)]
    [TestCase("COMMIT_EDITMSG", RepositoryChanges.None)]
    public void Classify_git_directory_entries(string entry, RepositoryChanges expected)
    {
        Classify(P([".git", .. entry.Split('/')]), out string? path).Should().Be(expected);
        path.Should().BeNull();
    }

    [Test]
    public void Classify_ignores_the_git_directory_itself_nested_repositories_and_other_folders()
    {
        Classify(P(".git"), out _).Should().Be(RepositoryChanges.None);
        Classify(P("sub", ".git", "index"), out _).Should().Be(RepositoryChanges.None);
        Classify(Path.Combine(Path.GetTempPath(), "elsewhere.txt"), out _).Should().Be(RepositoryChanges.None);
    }

    [Test]
    public void Classify_a_linked_worktree_reads_refs_from_the_common_git_directory()
    {
        string common = OperatingSystem.IsWindows() ? @"C:\main\.git" : "/main/.git";
        string own = Path.Combine(common, "worktrees", "wt");

        Classify(Path.Combine(common, "refs", "heads", "main"), out _, own, common).Should().Be(RepositoryChanges.Refs);
        Classify(Path.Combine(own, "HEAD"), out _, own, common).Should().Be(RepositoryChanges.All);
        Classify(Path.Combine(own, "index"), out _, own, common).Should().Be(RepositoryChanges.WorkTree);
    }

    [Test]
    public void Classify_matches_the_case_of_paths_like_the_platform()
    {
        RepositoryChanges changes = RepositoryWatcher.Classify(P("A.TXT"), Root.ToUpperInvariant(), [P(".git")], out _);

        changes.Should().Be(OperatingSystem.IsLinux() ? RepositoryChanges.None : RepositoryChanges.WorkTree);
    }

    [Test]
    public async Task Watcher_reports_a_burst_of_changes_once()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ge-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        ConcurrentQueue<RepositoryChangeBatch> batches = new();
        try
        {
            using RepositoryWatcher watcher = new(folder, [Path.Combine(folder, ".git")], batches.Enqueue, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
            watcher.IsReliable.Should().BeTrue(watcher.Problem);

            for (int i = 0; i < 40; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, $"f{i}.txt"), "x");
            }

            await File.WriteAllTextAsync(Path.Combine(folder, ".git", "index.lock"), "lock");
            await WaitUntilAsync(() => !batches.IsEmpty);
            await Task.Delay(600);

            batches.Should().ContainSingle();
            batches.Single().Changes.Should().Be(RepositoryChanges.WorkTree);
            batches.Single().WorkTreePaths.Should().Contain(["f0.txt", "f39.txt"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_file_created_outside_the_app_shows_up_without_a_refresh()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new FakeFolderPicker());
        await viewModel.OpenAsync(repository.Path);
        viewModel.ChangedFiles.Should().HaveCount(2);

        await File.WriteAllTextAsync(Path.Combine(repository.Path, "d.txt"), "new");

        await WaitUntilAsync(() => viewModel.ChangedFiles.Any(file => file.Name == "d.txt"));
        viewModel.ChangedFiles.Should().HaveCount(3);
    }

    [AvaloniaTest]
    public async Task A_commit_and_a_branch_made_on_the_command_line_update_the_history_and_branch()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new FakeFolderPicker());
        await viewModel.OpenAsync(repository.Path);
        viewModel.Commits.Should().HaveCount(2);

        repository.RunGit("checkout", "--quiet", "-b", "topic");
        repository.RunGit("add", "a.txt");
        repository.RunGit("-c", "user.name=Test Author", "-c", "user.email=test@example.com", "commit", "--quiet", "-m", "From the CLI");

        await WaitUntilAsync(() => viewModel.Branch == "topic" && viewModel.Commits.Count == 3 && viewModel.ChangedFiles.Count == 1 && !viewModel.IsBusy);
        viewModel.Commits[0].Subject.Should().Be("From the CLI");
        viewModel.ChangedFiles.Select(file => file.Name).Should().Equal("c.txt");
    }

    [AvaloniaTest]
    public async Task Focus_refresh_keeps_the_selection_when_nothing_changed()
    {
        using TestRepository repository = new();
        MainWindowViewModel viewModel = new(GitEnvironment.Initialize(), new FakeFolderPicker());
        await viewModel.OpenAsync(repository.Path);
        ChangedFileViewModel selected = viewModel.ChangedFiles.First(file => file.Name == "a.txt");
        viewModel.SelectedChangedFile = selected;
        int collectionChanges = 0;
        viewModel.ChangedFiles.CollectionChanged += (_, _) => collectionChanges++;

        viewModel.OnWindowActiveChanged(isActive: true);
        await Task.Delay(1000);

        viewModel.SelectedChangedFile.Should().BeSameAs(selected);
        collectionChanges.Should().Be(0);
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
}
