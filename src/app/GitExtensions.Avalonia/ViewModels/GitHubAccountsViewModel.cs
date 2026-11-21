using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>A signed-in GitHub account in the account chooser.</summary>
public sealed partial class GitHubAccountRow(string login, bool isActive, bool isRepositoryAccount) : ObservableObject
{
    public string Login { get; } = login;

    /// <summary>The account git uses by default (gh's active account).</summary>
    public bool IsActive { get; } = isActive;

    /// <summary>The account this repository is set to use.</summary>
    public bool IsRepositoryAccount { get; } = isRepositoryAccount;

    public string Note => (IsRepositoryAccount, IsActive) switch
    {
        (true, true) => "used by this repository · active",
        (true, false) => "used by this repository",
        (false, true) => "active",
        _ => string.Empty,
    };

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
///  Lists the GitHub accounts signed in on this computer and chooses the one a repository pushes with: for this
///  repository only (the default), or as the active account everywhere. Opened from Repository &gt; GitHub account…
///  and when a push fails because GitHub does not show the repository to the signed-in account.
/// </summary>
public sealed partial class GitHubAccountsViewModel(Func<GitModule?> moduleProvider, Action<string> reportStatus, Func<Task> pushAgain) : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Why the chooser opened by itself (a failed push), shown on top.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string? _hint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>Make the chosen account the active one for every repository, instead of this repository only.</summary>
    [ObservableProperty]
    private bool _useEverywhere;

    /// <summary>Push again once the account is chosen (when the chooser opened after a failed push).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText))]
    private bool _pushAfterwards;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepositoryAccount))]
    private string? _repositoryAccount;

    public ObservableCollection<GitHubAccountRow> Accounts { get; } = [];

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasRepositoryAccount => RepositoryAccount is not null;

    public string ApplyText => PushAfterwards ? "Use and push again" : "Use this account";

    /// <summary>Opens the chooser; <paramref name="failure"/> is the push error that made it open, if any.</summary>
    public async Task OpenAsync(string? failure = null)
    {
        Hint = failure is null
            ? null
            : "GitHub did not find this repository for the account the push used. If another of your accounts has access, choose it below.";
        PushAfterwards = failure is not null;
        UseEverywhere = false;
        IsOpen = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            GitModule? module = moduleProvider();
            (IReadOnlyList<GitHubAccount> accounts, string? mine) = await Task.Run(() =>
                (GitHubAccounts.List(), module is null ? null : GitHubAccounts.RepositoryAccount(module)));
            RepositoryAccount = mine;
            Accounts.Clear();
            foreach (GitHubAccount account in accounts)
            {
                bool isMine = string.Equals(account.Login, mine, StringComparison.OrdinalIgnoreCase);
                Accounts.Add(new GitHubAccountRow(account.Login, account.IsActive, isMine));
            }

            // Preselect a different account than the one that just failed (the repository's, else the active one).
            GitHubAccountRow? used = Accounts.FirstOrDefault(a => a.IsRepositoryAccount) ?? Accounts.FirstOrDefault(a => a.IsActive);
            GitHubAccountRow? pick = PushAfterwards ? Accounts.FirstOrDefault(a => !ReferenceEquals(a, used)) ?? used : used;
            if (pick is not null)
            {
                pick.IsSelected = true;
            }

            if (Accounts.Count == 0)
            {
                Error = "No GitHub account is signed in. Use \"Add account\" to sign in.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Accounts.FirstOrDefault(a => a.IsSelected) is not { } chosen || moduleProvider() is not { } module)
        {
            return;
        }

        Error = null;
        try
        {
            bool everywhere = UseEverywhere;
            await Task.Run(() =>
            {
                if (everywhere)
                {
                    GitHubAccounts.ClearRepositoryAccount(module);
                    GitHubAccounts.SwitchActive(chosen.Login);
                }
                else
                {
                    GitHubAccounts.UseForRepository(module, chosen.Login);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
            return;
        }

        reportStatus(UseEverywhere ? $"{chosen.Login} is now the active GitHub account" : $"This repository now uses the GitHub account {chosen.Login}");
        bool push = PushAfterwards;
        IsOpen = false;
        if (push)
        {
            await pushAgain();
        }
    }

    /// <summary>Goes back to the active account for this repository.</summary>
    [RelayCommand]
    private async Task UseActiveAccountAsync()
    {
        if (moduleProvider() is { } module)
        {
            await Task.Run(() => GitHubAccounts.ClearRepositoryAccount(module));
            reportStatus("This repository now uses the active GitHub account");
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void AddAccount()
    {
        try
        {
            GitHubAccounts.StartSignIn();
            Hint = "Finish signing in in the window that opened, then press Refresh.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Error = $"Could not start the GitHub CLI (gh): {ex.Message}";
        }
    }

    [RelayCommand]
    private void Select(GitHubAccountRow? row)
    {
        foreach (GitHubAccountRow each in Accounts)
        {
            each.IsSelected = ReferenceEquals(each, row);
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
