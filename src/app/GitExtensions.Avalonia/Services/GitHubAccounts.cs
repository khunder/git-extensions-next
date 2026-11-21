using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitCommands;
using GitExtensions.Extensibility;

namespace GitExtensions.Desktop.Services;

/// <summary>A GitHub account signed in on this computer.</summary>
/// <param name="Login">The GitHub user name.</param>
/// <param name="IsActive">The account git uses by default for github.com.</param>
public sealed record GitHubAccount(string Login, bool IsActive);

/// <summary>
///  The GitHub accounts signed in through the GitHub CLI (<c>gh</c>), and which one a repository pushes with.
/// </summary>
/// <remarks>
///  <para>
///   gh can hold several accounts per host, but its git credential helper only hands out the token of the active one.
///   So a push to a repository that only another account can see fails with GitHub's "Repository not found".
///  </para>
///  <para>
///   Two ways out, both reversible:
///   <list type="bullet">
///    <item>For one repository: a credential helper in the repository's own config asks gh for that account's token
///    (<c>gh auth token --user</c>) when git needs it. No token is written anywhere; other repositories are unchanged.</item>
///    <item>Everywhere: <c>gh auth switch</c> makes the account the active one.</item>
///   </list>
///  </para>
/// </remarks>
public static partial class GitHubAccounts
{
    private const string Host = "github.com";
    private const string HelperKey = "credential.https://github.com.helper";
    private const string UserNameKey = "credential.https://github.com.username";

    // GitHub logins: letters, digits and single hyphens. Checked before a login goes into a shell command.
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex LoginPattern();

    /// <summary>True when <paramref name="login"/> can be a GitHub user name.</summary>
    public static bool IsValidLogin(string login) => LoginPattern().IsMatch(login);

    /// <summary>The accounts gh is signed in with for github.com (no tokens are read).</summary>
    /// <exception cref="InvalidOperationException">gh is not installed or failed; the message says what to do.</exception>
    public static IReadOnlyList<GitHubAccount> List()
    {
        (int exitCode, string output, string error) = RunGh("auth", "status", "--hostname", Host, "--json", "hosts");
        if (exitCode != 0 && output.Length == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The GitHub CLI (gh) reported an error." : error.Trim());
        }

        return ParseStatus(output);
    }

    /// <summary>Reads the accounts out of <c>gh auth status --json hosts</c>.</summary>
    public static IReadOnlyList<GitHubAccount> ParseStatus(string json)
    {
        List<GitHubAccount> accounts = [];
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("hosts", out JsonElement hosts)
            && hosts.TryGetProperty(Host, out JsonElement entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("login", out JsonElement login) && login.GetString() is { Length: > 0 } name
                    && (!entry.TryGetProperty("state", out JsonElement state) || state.GetString() == "success"))
                {
                    accounts.Add(new GitHubAccount(name, entry.TryGetProperty("active", out JsonElement active) && active.ValueKind == JsonValueKind.True));
                }
            }
        }

        return accounts;
    }

    /// <summary>The account this repository is set to push with, or <see langword="null"/> when it uses the active one.</summary>
    public static string? RepositoryAccount(GitModule module)
    {
        ExecutionResult result = module.GitExecutable.Execute($"config --local --get {UserNameKey}", throwOnErrorExit: false);
        string login = result.StandardOutput.Trim();
        return result.ExitedSuccessfully && IsValidLogin(login) ? login : null;
    }

    /// <summary>Makes this repository push and pull with <paramref name="login"/>, whatever account is active.</summary>
    public static void UseForRepository(GitModule module, string login)
    {
        if (!IsValidLogin(login))
        {
            throw new ArgumentException($"'{login}' is not a GitHub user name.", nameof(login));
        }

        ClearRepositoryAccount(module);

        // An empty helper first resets the helpers inherited from the global config (gh's, which serves the active account).
        Git(module, $"config --local --add {HelperKey} \"\"");
        string helper = $"!f() {{ test \\\"$1\\\" = get || exit 0; echo username={login}; echo \\\"password=$(gh auth token --hostname {Host} --user {login})\\\"; }}; f";
        Git(module, $"config --local --add {HelperKey} \"{helper}\"");
        Git(module, $"config --local {UserNameKey} {login}");
    }

    /// <summary>Goes back to the active account for this repository.</summary>
    public static void ClearRepositoryAccount(GitModule module)
    {
        _ = module.GitExecutable.Execute($"config --local --unset-all {HelperKey}", throwOnErrorExit: false);
        _ = module.GitExecutable.Execute($"config --local --unset-all {UserNameKey}", throwOnErrorExit: false);
    }

    /// <summary>Makes <paramref name="login"/> the active account for every repository (<c>gh auth switch</c>).</summary>
    public static void SwitchActive(string login)
    {
        if (!IsValidLogin(login))
        {
            throw new ArgumentException($"'{login}' is not a GitHub user name.", nameof(login));
        }

        (int exitCode, _, string error) = RunGh("auth", "switch", "--hostname", Host, "--user", login);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(error.Trim());
        }
    }

    /// <summary>Opens gh's sign-in (browser based) in a console window, to add another account.</summary>
    public static void StartSignIn()
        => Process.Start(new ProcessStartInfo("gh", $"auth login --hostname {Host} --web --git-protocol https") { UseShellExecute = true });

    /// <summary>
    ///  True for a push or pull that failed because GitHub hides a repository the signed-in account cannot see
    ///  ("Repository not found", or a 403 for an account without write access).
    /// </summary>
    public static bool IsAccountProblem(string failure)
        => failure.Contains("github.com", StringComparison.OrdinalIgnoreCase)
           && (failure.Contains("Repository not found", StringComparison.OrdinalIgnoreCase)
               || failure.Contains("Permission to", StringComparison.OrdinalIgnoreCase)
               || failure.Contains("403", StringComparison.Ordinal));

    private static void Git(GitModule module, string arguments)
    {
        ExecutionResult result = module.GitExecutable.Execute(arguments, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }
    }

    private static (int ExitCode, string Output, string Error) RunGh(params string[] arguments)
    {
        ProcessStartInfo start = new("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the GitHub CLI (gh).");

            // stderr is collected by events so a full pipe cannot block the stdout read.
            System.Text.StringBuilder error = new();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (error)
                    {
                        error.AppendLine(e.Data);
                    }
                }
            };
            process.BeginErrorReadLine();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            lock (error)
            {
                return (process.ExitCode, output, error.ToString());
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("The GitHub CLI (gh) is not installed. Install it from https://cli.github.com to manage GitHub accounts.");
        }
    }
}
