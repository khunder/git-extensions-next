using GitCommands;
using GitExtensions.Desktop.Services;

namespace GitExtensionsDesktopTests;

[TestFixture]
public class GitHubAccountsTests
{
    [Test]
    public void Status_lists_the_signed_in_accounts_and_the_active_one_without_tokens()
    {
        const string json = """
            {"hosts":{"github.com":[
              {"state":"success","active":true,"host":"github.com","login":"rassil-zembra","tokenSource":"keyring"},
              {"state":"success","active":false,"host":"github.com","login":"Rassilo","tokenSource":"keyring"},
              {"state":"error","active":false,"host":"github.com","login":"expired-one"}
            ]}}
            """;

        GitHubAccounts.ParseStatus(json).Should().Equal(
            new GitHubAccount("rassil-zembra", true),
            new GitHubAccount("Rassilo", false));
    }

    [TestCase("remote: Repository not found.\nfatal: repository 'https://github.com/acme/app.git/' not found", true)]
    [TestCase("remote: Permission to acme/app.git denied to someone.\nfatal: unable to access 'https://github.com/acme/app.git/': The requested URL returned error: 403", true)]
    [TestCase("fatal: repository 'https://gitlab.com/acme/app.git/' not found", false)]
    [TestCase("! [rejected] main -> main (fetch first)", false)]
    public void Account_problems_are_recognised(string failure, bool expected)
        => GitHubAccounts.IsAccountProblem(failure).Should().Be(expected);

    [TestCase("Rassilo", true)]
    [TestCase("rassil-zembra", true)]
    [TestCase("x; rm -rf /", false)]
    [TestCase("-leading", false)]
    public void Only_GitHub_user_names_are_accepted(string login, bool expected)
        => GitHubAccounts.IsValidLogin(login).Should().Be(expected);

    [Avalonia.Headless.NUnit.AvaloniaTest]
    public void A_repository_can_use_its_own_account_and_go_back_to_the_active_one()
    {
        using TestRepository repository = new();
        GitModule module = GitEnvironment.Initialize().OpenRepository(repository.Path)!;

        GitHubAccounts.RepositoryAccount(module).Should().BeNull();

        GitHubAccounts.UseForRepository(module, "Rassilo");

        GitHubAccounts.RepositoryAccount(module).Should().Be("Rassilo");
        string[] helpers = repository.GitOutput("config", "--local", "--get-all", "credential.https://github.com.helper")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        helpers.Should().HaveCount(1, "the empty helper is filtered out here; the second one asks gh for the account's token");
        helpers[0].Should().Be("!f() { test \"$1\" = get || exit 0; echo username=Rassilo; echo \"password=$(gh auth token --hostname github.com --user Rassilo)\"; }; f");

        GitHubAccounts.ClearRepositoryAccount(module);

        GitHubAccounts.RepositoryAccount(module).Should().BeNull();
        repository.GitOutput("config", "--local", "--get-all", "credential.https://github.com.helper").Should().BeEmpty();
    }
}
