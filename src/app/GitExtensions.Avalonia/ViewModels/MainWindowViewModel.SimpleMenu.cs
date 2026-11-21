using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

// The menu bar of the simple mode, like GitHub Desktop's: File, View, Repository, Branch and Help, with the everyday
// actions in plain words. Each entry reuses a command of the full interface where one exists.
public sealed partial class MainWindowViewModel
{
    /// <summary>A remote of the repository lives on GitHub (<see cref="GitHubWebUrl"/> is known).</summary>
    public bool HasGitHubRemote => GitHubWebUrl is not null;

    private GitHubAccountsViewModel? _gitHubAccounts;

    /// <summary>The GitHub account chooser (Repository &gt; GitHub account…, and after a push GitHub refused).</summary>
    public GitHubAccountsViewModel GitHubAccounts
        => _gitHubAccounts ??= new GitHubAccountsViewModel(() => _module, message => Status = message, () => Remote.PushCommand.ExecuteAsync(null));

    [RelayCommand]
    private Task OpenGitHubAccountsAsync() => GitHubAccounts.OpenAsync();

    // GitHub answers "Repository not found" when the account used cannot see the repository: offer the other accounts.
    private void OnRemoteOperationFailed(string title, string failure)
    {
        if (title == "Push" && Services.GitHubAccounts.IsAccountProblem(failure))
        {
            _ = GitHubAccounts.OpenAsync(failure);
        }
    }

    [RelayCommand]
    private Task PushNowAsync() => Remote.PushCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task PullNowAsync() => Remote.PullCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task FetchNowAsync() => Remote.FetchCommand.ExecuteAsync(null);

    [RelayCommand]
    private void RenameCurrentBranch()
    {
        if (Branch is { } branch && Refs.FindNode(RefNodeKind.LocalBranch, branch) is { } node)
        {
            Refs.RequestRenameCommand.Execute(node);
        }
    }

    /// <summary>Shows the Changes tab and asks to discard every change (the tab asks for confirmation first).</summary>
    [RelayCommand]
    private void DiscardAllChanges()
    {
        SelectSidebarTab(ChangesTabIndex);
        EnsureChangesTab();
        ChangesTab?.DiscardAll();
    }

    [RelayCommand]
    private async Task StashAllChangesAsync()
    {
        SelectSidebarTab(ChangesTabIndex);
        EnsureChangesTab();
        if (ChangesTab is { } tab)
        {
            await tab.StashAllCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ShowChangesTab() => SelectSidebarTab(ChangesTabIndex);

    [RelayCommand]
    private void ShowHistoryTab() => SelectSidebarTab(HistoryTabIndex);

    [RelayCommand]
    private void ViewOnGitHub() => OpenGitHub(string.Empty);

    [RelayCommand]
    private void CreateIssueOnGitHub() => OpenGitHub("/issues/new");

    [RelayCommand]
    private void ViewBranchOnGitHub() => OpenGitHub($"/tree/{Uri.EscapeDataString(Branch ?? string.Empty)}");

    [RelayCommand]
    private void CreatePullRequestOnGitHub() => OpenGitHub($"/compare/{Uri.EscapeDataString(Branch ?? string.Empty)}?expand=1");

    [RelayCommand]
    private void OpenInVisualStudioCode()
    {
        if (_module is not { } module)
        {
            return;
        }

        try
        {
            // "code" is a .cmd on Windows: start it through the shell so PATH lookup finds it.
            Process.Start(new ProcessStartInfo("code", $"\"{module.WorkingDir}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorMessage = $"Could not start Visual Studio Code: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow?.Close();
        }
    }

    private void OpenGitHub(string path)
    {
        if (GitHubWebUrl is { } url)
        {
            ShellLauncher.OpenUrl(url + path);
        }
    }
}
