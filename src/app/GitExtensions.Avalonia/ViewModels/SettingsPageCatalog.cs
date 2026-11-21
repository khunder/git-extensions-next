using GitCommands;
using GitCommands.Settings;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  Builds the tree of settings pages of the original Settings dialog. Rows are bound to the same persisted settings the
///  original uses (<c>AppSettings</c>, git config); pages for features this app does not have show a notice instead.
/// </summary>
public static partial class SettingsPageCatalog
{
    private const string NotApplied = "These options are stored in the shared Git Extensions settings; this application does not use them yet.";

    public static IReadOnlyList<SettingsPageViewModel> Build(SettingsContext context)
    {
        ChecklistViewModel checklist = new(context.RepositoryDirectory);
        SettingsPageViewModel root = new(SettingsPageIds.Checklist, "Git Extensions Next", "GitLogo16") { Checklist = checklist };

        SettingsPageViewModel appearance = new SettingsPageViewModel(SettingsPageIds.Appearance, "Appearance", "Appearance")
            .Add(Sorting())
            .Add(Colors())
            .Add(Fonts())
            .Add(Notice(SettingsPageIds.ConsoleStyle, "Console style", "Console", "The embedded console emulator (ConEmu) is Windows only; commands run in the Output panel."));
        AppearanceRows(appearance);

        SettingsPageViewModel advanced = new SettingsPageViewModel(SettingsPageIds.Advanced, "Advanced", "AdvancedSettings")
            .Add(Confirmations());
        AdvancedRows(advanced);

        SettingsPageViewModel detailed = new SettingsPageViewModel(SettingsPageIds.Detailed, "Detailed", "Settings")
            .Add(BrowseRepository())
            .Add(CommitDialog())
            .Add(DiffViewer())
            .Add(BlameViewer());
        DetailedRows(detailed);

        SettingsPageViewModel general = new(SettingsPageIds.General, "General", "GeneralSettings");
        GeneralRows(general, context);

        root.Add(general)
            .Add(appearance)
            .Add(Notice(SettingsPageIds.RevisionLinks, "Revision links", "Link", "Defining links from commit messages to issue trackers is not supported in this version."))
            .Add(Notice(SettingsPageIds.BuildServer, "Build server integration", "Integration", "Build server integration is not supported in this version."))
            .Add(Notice(SettingsPageIds.Scripts, "Scripts", "cmd", "User scripts are not supported in this version."))
            .Add(Notice(SettingsPageIds.Hotkeys, "Hotkeys", "Hotkey", "Editing hotkeys is not supported in this version. The current shortcuts are listed by Help > Keyboard shortcuts."))
            .Add(Notice(SettingsPageIds.ShellExtension, "Shell extension", "ShellExtensions", "The Windows Explorer shell extension is Windows only and does not exist on this platform."))
            .Add(advanced)
            .Add(detailed)
            .Add(Ssh());

        SettingsPageViewModel git = new SettingsPageViewModel(SettingsPageIds.Git, "Git", "GitLogo16")
            .Add(GitPaths(context))
            .Add(GitConfig(context, advancedPage: false))
            .Add(GitConfig(context, advancedPage: true));
        git.With(new NoticeRow("Settings that control how Git itself behaves. Use the paths page if git is not found."));

        SettingsPageViewModel plugins = new(SettingsPageIds.Plugins, "Plugins", "plugin");
        plugins.With(new NoticeRow("Not available yet. Plugins are not supported in this version.", warning: true));

        // The Git and Plugins nodes of the original are siblings of the root: three top-level nodes.
        return [root, git, plugins];
    }

    private static SettingsPageViewModel Notice(string id, string title, string icon, string text)
        => new SettingsPageViewModel(id, title, icon).With(new NoticeRow("Not available yet. " + text, warning: true));

    private static BoolRow B(string label, Func<bool> get, Action<bool> set, string? description = null) => new(label, get, set, true, description);

    private static BoolRow B(string label, BooleanSetting setting, string? description = null) => new(label, () => setting.Value, v => setting.Value = v, true, description);

    // Confirmation settings are stored as "don't confirm"; the page shows "confirm".
    private static BoolRow Confirm(string label, BooleanSetting dontConfirm) => new(label, () => !dontConfirm.Value, v => dontConfirm.Value = !v);

    private static TextRow T(string label, Func<string> get, Action<string> set, BrowseKind browse = BrowseKind.None, string? watermark = null)
        => new(label, get, set) { Browse = browse, Watermark = watermark };

    private static IntRow I(string label, Func<int> get, Action<int> set, int min, int max, string? description = null) => new(label, get, set, min, max, true, description);

    private static NoticeRow NotAppliedNotice() => new(NotApplied);

    private static void SetAutoRefreshOnFileChanges(bool value)
    {
        AppSettings.AutoRefreshOnFileChanges = value;
        RepositoryWatcher.NotifySettingChanged();
    }

    private static void GeneralRows(SettingsPageViewModel page, SettingsContext context)
    {
        BehaviorSettings behavior = context.Behavior;
        page.With(
            new HeaderRow("Revision history"),
            new IntRow(
                "Maximum number of commits loaded into the history list",
                () => behavior.HistoryLimit,
                behavior.SetHistoryLimit,
                1,
                BehaviorSettings.MaxHistoryLimit,
                immediate: false,
                description: "Applies when the repository is reloaded; applying the page reloads it for you.")
            {
                Keywords = "history limit commits",
            },
            new BoolRow("Show stashes in history", () => AppSettings.ShowStashes, v => AppSettings.ShowStashes = v, immediate: false),
            B("Follow renames in file history", () => AppSettings.FollowRenamesInFileHistory, v => AppSettings.FollowRenamesInFileHistory = v),
            B("Follow renames: exact matches only", () => AppSettings.FollowRenamesInFileHistoryExactOnly, v => AppSettings.FollowRenamesInFileHistoryExactOnly = v),
            I("Quick search timeout (ms)", () => AppSettings.RevisionGridQuickSearchTimeout, v => AppSettings.RevisionGridQuickSearchTimeout = v, 100, 10000),
            new HeaderRow("Automatic refresh"),
            B("Automatically refresh on file changes", () => AppSettings.AutoRefreshOnFileChanges, SetAutoRefreshOnFileChanges,
                "Watches the working tree and the .git folder and updates the changes, branches and history without F5."),
            B("Refresh when the window gains focus", () => AppSettings.RefreshOnWindowActivated, v => AppSettings.RefreshOnWindowActivated = v,
                "Checks the status again when you come back to the app; catches what the file watcher missed."),
            new HeaderRow("Dashboard"),
            I("Number of top repositories", () => AppSettings.MaxTopRepositories, v => AppSettings.MaxTopRepositories = v, 0, 100),
            I("Recent repositories history size", () => AppSettings.RecentRepositoriesHistorySize, v => AppSettings.RecentRepositoriesHistorySize = v, 0, 200),
            B("Sort top repositories alphabetically", () => AppSettings.SortTopRepos, v => AppSettings.SortTopRepos = v),
            B("Sort recent repositories alphabetically", () => AppSettings.SortRecentRepos, v => AppSettings.SortRecentRepos = v),
            B("Hide top repositories from the recent list", AppSettings.HideTopRepositoriesFromRecentList),
            ChoiceRow.ForEnum("Shorten recent repository paths", () => AppSettings.ShorteningRecentRepoPathStrategy, v => AppSettings.ShorteningRecentRepoPathStrategy = v),
            B("Open the last working directory on startup", () => AppSettings.StartWithRecentWorkingDir, v => AppSettings.StartWithRecentWorkingDir = v),
            T("Default clone destination", () => AppSettings.DefaultCloneDestinationPath, v => AppSettings.DefaultCloneDestinationPath = v, BrowseKind.Folder),
            new HeaderRow("Commands"),
            B("Use histogram diff algorithm", () => AppSettings.UseHistogramDiffAlgorithm, v => AppSettings.UseHistogramDiffAlgorithm = v),
            B("Include untracked files in automatic stash", () => AppSettings.IncludeUntrackedFilesInAutoStash, v => AppSettings.IncludeUntrackedFilesInAutoStash = v),
            B("Check for uncommitted changes in the checkout branch dialog", () => AppSettings.CheckForUncommittedChangesInCheckoutBranch, v => AppSettings.CheckForUncommittedChangesInCheckoutBranch = v),
            B("Close the process dialog when the process succeeds", () => AppSettings.CloseProcessDialog, v => AppSettings.CloseProcessDialog = v),
            B("Show the git command line in the process dialog", () => AppSettings.ShowGitCommandLine, v => AppSettings.ShowGitCommandLine = v),
            ChoiceRow.ForEnum("Default pull action", () => AppSettings.DefaultPullAction, v => AppSettings.DefaultPullAction = v),
            ChoiceRow.ForNullableBool("Update submodules on checkout", "Ask", "Always", "Never", () => AppSettings.UpdateSubmodulesOnCheckout, v => AppSettings.UpdateSubmodulesOnCheckout = v),
            new HeaderRow("Browse window"),
            B("Show stash count", () => AppSettings.ShowStashCount, v => AppSettings.ShowStashCount = v),
            B("Show ahead and behind data", () => AppSettings.ShowAheadBehindData, v => AppSettings.ShowAheadBehindData = v),
            B("Show git status in the toolbar", () => AppSettings.ShowGitStatusInBrowseToolbar, v => AppSettings.ShowGitStatusInBrowseToolbar = v),
            B("Show git status for artificial commits", () => AppSettings.ShowGitStatusForArtificialCommits, v => AppSettings.ShowGitStatusForArtificialCommits = v),
            B("Show submodule status", () => AppSettings.ShowSubmoduleStatus, v => AppSettings.ShowSubmoduleStatus = v),
            NotAppliedNotice());

        // Both options change what the history shows: tell the host to reload the repository.
        foreach (SettingRow row in page.Rows)
        {
            switch (row)
            {
                case BoolRow { Label: "Show stashes in history" } stashes:
                    stashes.Changed += _ => context.HistoryChanged();
                    break;
                case IntRow limit when limit.Label.StartsWith("Maximum", StringComparison.Ordinal):
                    limit.Changed += _ => context.HistoryChanged();
                    break;
            }
        }
    }

    private static void AppearanceRows(SettingsPageViewModel page)
    {
        page.With(
            B("Show relative date instead of full date", () => AppSettings.RelativeDate, v => AppSettings.RelativeDate = v),
            B("Show the current branch in the window title", () => AppSettings.ShowRepoCurrentBranch, v => AppSettings.ShowRepoCurrentBranch = v),
            ChoiceRow.ForEnum("Truncate long paths", () => AppSettings.TruncatePathMethod, v => AppSettings.TruncatePathMethod = v),
            B("Show author avatar in the commit info", () => AppSettings.ShowAuthorAvatarInCommitInfo, v => AppSettings.ShowAuthorAvatarInCommitInfo = v),
            B("Show author avatar column in the revision grid", () => AppSettings.ShowAuthorAvatarColumn, v => AppSettings.ShowAuthorAvatarColumn = v),
            I("Days to cache avatar images", () => AppSettings.AvatarImageCacheDays, v => AppSettings.AvatarImageCacheDays = v, 0, 365),
            T("Language", () => AppSettings.Translation, v => AppSettings.Translation = v, watermark: "English"),
            new NoticeRow("The user interface is only available in English; the language choice is stored for compatibility."),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel Sorting()
    {
        SettingsPageViewModel page = new(SettingsPageIds.Sorting, "Sorting", "SortBy");
        return page.With(
            new ChoiceRow(
                "Sort revisions by",
                ["Git default", "Author date", "Topology"],
                () => AppSettings.RevisionSortOrder.Value switch { RevisionSortOrder.AuthorDate => "Author date", RevisionSortOrder.Topology => "Topology", _ => "Git default" },
                v =>
                {
                    AppSettings.RevisionSortOrder.Value = v switch { "Author date" => RevisionSortOrder.AuthorDate, "Topology" => RevisionSortOrder.Topology, _ => RevisionSortOrder.GitDefault };
                    AppSettings.RevisionSortOrder.Save();
                }),
            ChoiceRow.ForEnum("Sort branches by", () => AppSettings.RefsSortBy, v => AppSettings.RefsSortBy = v),
            ChoiceRow.ForEnum("Branch order", () => AppSettings.RefsSortOrder, v => AppSettings.RefsSortOrder = v),
            T("Prioritized branch names", () => AppSettings.PrioritizedBranchNames, v => AppSettings.PrioritizedBranchNames = v, watermark: "main;master;develop"),
            T("Prioritized remote names", () => AppSettings.PrioritizedRemoteNames, v => AppSettings.PrioritizedRemoteNames = v, watermark: "origin;upstream"),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel Colors()
    {
        SettingsPageViewModel page = new(SettingsPageIds.Colors, "Colors", "Colors");
        return page.With(
            new HeaderRow("Theme"),
            ChoiceRow.ForEnum("Application theme", () => ThemeService.Current.Choice, v => ThemeService.Current.Set(v), description: "Applies immediately."),
            new HeaderRow("Revision graph"),
            B("Use multiple colors for branches", () => AppSettings.MulticolorBranches, v => AppSettings.MulticolorBranches = v),
            B("Highlight authored revisions", () => AppSettings.HighlightAuthoredRevisions, v => AppSettings.HighlightAuthoredRevisions = v),
            B("Fill ref labels", () => AppSettings.FillRefLabels, v => AppSettings.FillRefLabels = v),
            B("Draw alternate background color for rows", () => AppSettings.RevisionGraphDrawAlternateBackColor, SetAlternateRows),
            new ColorRow("Alternate row color", () => RowColors.EffectiveNormal, c => RowColors.SetNormal(c.ToString()), () => RowColors.SetNormal(string.Empty), "The stripe of every other row in the revision grid."),
            new ColorRow("Alternate row color of your commits", () => RowColors.EffectiveMine, c => RowColors.SetMine(c.ToString()), () => RowColors.SetMine(string.Empty), "The stripe of every other row of the commits you authored."),
            B("Draw non-relatives in gray", () => AppSettings.RevisionGraphDrawNonRelativesGray, v => AppSettings.RevisionGraphDrawNonRelativesGray = v),
            B("Draw non-relatives text in gray", () => AppSettings.RevisionGraphDrawNonRelativesTextGray, v => AppSettings.RevisionGraphDrawNonRelativesTextGray = v),
            new NoticeRow("Pick the alpha in the color picker for a more or less subtle stripe; Use default goes back to the theme. The other colors follow the theme."),
            NotAppliedNotice());
    }

    private static void SetAlternateRows(bool value)
    {
        AppSettings.RevisionGraphDrawAlternateBackColor = value;
        RowColors.NotifyEnabledChanged();
    }

    private static SettingsPageViewModel Fonts()
        => Notice(SettingsPageIds.Fonts, "Fonts", "Font", "Choosing fonts is not supported in this version; the application uses the system fonts.");

    private static void AdvancedRows(SettingsPageViewModel page)
    {
        page.With(
            B("Always show the checkout branch dialog", () => AppSettings.AlwaysShowCheckoutBranchDlg, v => AppSettings.AlwaysShowCheckoutBranchDlg = v),
            B("Use the default local changes action when checking out", () => AppSettings.UseDefaultCheckoutBranchAction, v => AppSettings.UseDefaultCheckoutBranchAction = v),
            B("Do not show help images", () => AppSettings.DontShowHelpImages, v => AppSettings.DontShowHelpImages = v),
            B("Always show advanced options", () => AppSettings.AlwaysShowAdvOpt, v => AppSettings.AlwaysShowAdvOpt = v),
            B("Check for updates", () => AppSettings.CheckForUpdates, v => AppSettings.CheckForUpdates = v),
            B("Include release candidates in the update check", () => AppSettings.CheckForReleaseCandidates, v => AppSettings.CheckForReleaseCandidates = v),
            B("Normalise branch names", AppSettings.AutoNormaliseBranchName),
            T("Branch name replacement symbol", () => AppSettings.AutoNormaliseSymbol, v => AppSettings.AutoNormaliseSymbol = v),
            B("Force the commit and push option when amending", () => AppSettings.CommitAndPushForcedWhenAmend, v => AppSettings.CommitAndPushForcedWhenAmend = v),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel Confirmations()
    {
        SettingsPageViewModel page = new(SettingsPageIds.Confirmations, "Confirmations", "StatusBadgeSuccess");
        return page.With(
            new NoticeRow("Ask for confirmation before:"),
            Confirm("Amending a commit", AppSettings.DontConfirmAmend),
            Confirm("Undoing the last commit", AppSettings.DontConfirmUndoLastCommit),
            Confirm("Committing when no branch is checked out", AppSettings.DontConfirmCommitIfNoBranch),
            Confirm("Rebasing on top of the selected commit", AppSettings.DontConfirmRebase),
            Confirm("Fetching and pruning all remotes", AppSettings.DontConfirmFetchAndPruneAll),
            Confirm("Pushing a new branch", AppSettings.DontConfirmPushNewBranch),
            Confirm("Adding a tracking reference when pushing", AppSettings.DontConfirmAddTrackingRef),
            Confirm("Deleting an unmerged branch", AppSettings.DontConfirmDeleteUnmergedBranch),
            new BoolRow("Checking out a branch", () => AppSettings.ConfirmBranchCheckout.Value, v => AppSettings.ConfirmBranchCheckout.Value = v),
            new BoolRow("Dropping a stash", () => !AppSettings.DontConfirmStashDrop.Value, v => AppSettings.DontConfirmStashDrop.Value = !v),
            Confirm("Resolving conflicts", AppSettings.DontConfirmResolveConflicts),
            Confirm("Committing after conflicts are resolved", AppSettings.DontConfirmCommitAfterConflictsResolved),
            Confirm("Aborting a second time", AppSettings.DontConfirmSecondAbortConfirmation),
            Confirm("Switching worktree", AppSettings.DontConfirmSwitchWorktree),
            ChoiceRow.ForNullableBool("Pop the stash after pull", "Ask", "Always", "Never", () => AppSettings.AutoPopStashAfterPull, v => AppSettings.AutoPopStashAfterPull = v),
            ChoiceRow.ForNullableBool("Pop the stash after checkout", "Ask", "Always", "Never", () => AppSettings.AutoPopStashAfterCheckoutBranch, v => AppSettings.AutoPopStashAfterCheckoutBranch = v),
            NotAppliedNotice());
    }

    private static void DetailedRows(SettingsPageViewModel page)
    {
        page.With(
            B("Merge graph lanes having a common parent", AppSettings.MergeGraphLanesHavingCommonParent),
            B("Render the graph with diagonals", AppSettings.RenderGraphWithDiagonals),
            B("Straighten graph diagonals", AppSettings.StraightenGraphDiagonals),
            B("Show the password input in the process dialog", AppSettings.ShowProcessDialogPasswordInput),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel BrowseRepository()
    {
        SettingsPageViewModel page = new(SettingsPageIds.BrowseRepository, "Browse repository", "FileTree");
        return page.With(
            B("Use the browse window for file history", AppSettings.UseBrowseForFileHistory),
            B("Use the diff viewer for blame", AppSettings.UseDiffViewerForBlame),
            B("Show GPG information", AppSettings.ShowGpgInformation),
            B("Show the console tab", AppSettings.ShowConEmuTab),
            B("Show the output history as a tab", AppSettings.ShowOutputHistoryAsTab),
            I("Output history depth", () => AppSettings.OutputHistoryDepth.Value, v => AppSettings.OutputHistoryDepth.Value = v, 0, 100),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel CommitDialog()
    {
        SettingsPageViewModel page = new(SettingsPageIds.CommitDialog, "Commit dialog", "CommitSummary");
        return page.With(
            B("Show errors when staging files", () => AppSettings.ShowErrorsWhenStagingFiles, v => AppSettings.ShowErrorsWhenStagingFiles = v),
            B("Ensure the second line of the commit message is empty", () => AppSettings.EnsureCommitMessageSecondLineEmpty, v => AppSettings.EnsureCommitMessageSecondLineEmpty = v),
            B("Write the commit message in the commit window", () => AppSettings.UseFormCommitMessage, v => AppSettings.UseFormCommitMessage = v),
            I("Number of previous messages to remember", () => AppSettings.CommitDialogNumberOfPreviousMessages, v => AppSettings.CommitDialogNumberOfPreviousMessages = v, 0, 100),
            B("Show the Commit and push button", () => AppSettings.ShowCommitAndPush, v => AppSettings.ShowCommitAndPush = v),
            B("Show the Reset working tree changes button", () => AppSettings.ShowResetWorkTreeChanges, v => AppSettings.ShowResetWorkTreeChanges = v),
            B("Show the Reset all changes button", () => AppSettings.ShowResetAllChanges, v => AppSettings.ShowResetAllChanges = v),
            B("Provide auto-completion in the commit message", () => AppSettings.ProvideAutocompletion, v => AppSettings.ProvideAutocompletion = v),
            B("Remember the amend commit state", () => AppSettings.RememberAmendCommitState, v => AppSettings.RememberAmendCommitState = v),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel DiffViewer()
    {
        SettingsPageViewModel page = new(SettingsPageIds.DiffViewer, "Diff viewer", "Diff");
        return page.With(
            B("Remember the ignore whitespace preference", () => AppSettings.RememberIgnoreWhiteSpacePreference, v => AppSettings.RememberIgnoreWhiteSpacePreference = v),
            B("Remember the show entire file preference", () => AppSettings.RememberShowEntireFilePreference, v => AppSettings.RememberShowEntireFilePreference = v),
            B("Remember the show non-printing characters preference", () => AppSettings.RememberShowNonPrintingCharsPreference, v => AppSettings.RememberShowNonPrintingCharsPreference = v),
            B("Remember the number of context lines", () => AppSettings.RememberNumberOfContextLines, v => AppSettings.RememberNumberOfContextLines = v),
            B("Omit uninteresting changes from combined diffs", () => AppSettings.OmitUninterestingDiff, v => AppSettings.OmitUninterestingDiff = v),
            B("Open submodule diffs in a separate window", () => AppSettings.OpenSubmoduleDiffInSeparateWindow, v => AppSettings.OpenSubmoduleDiffInSeparateWindow = v),
            B("Show diffs for all parents", () => AppSettings.ShowDiffForAllParents, v => AppSettings.ShowDiffForAllParents = v),
            B("Show all available diff tools", () => AppSettings.ShowAvailableDiffTools, v => AppSettings.ShowAvailableDiffTools = v),
            I("Vertical ruler position (0 for none)", () => AppSettings.DiffVerticalRulerPosition, v => AppSettings.DiffVerticalRulerPosition = v, 0, 500),
            B("Use git coloring", AppSettings.UseGitColoring),
            B("Reverse the git coloring for the theme", AppSettings.ReverseGitColoring),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel BlameViewer()
    {
        SettingsPageViewModel page = new(SettingsPageIds.BlameViewer, "Blame viewer", "Blame");
        return page.With(
            B("Ignore whitespace changes", () => AppSettings.IgnoreWhitespaceOnBlame, v => AppSettings.IgnoreWhitespaceOnBlame = v),
            B("Detect moved or copied lines within the file", () => AppSettings.DetectCopyInFileOnBlame, v => AppSettings.DetectCopyInFileOnBlame = v, "Slower on big files."),
            B("Detect moved or copied lines across all files", () => AppSettings.DetectCopyInAllOnBlame, v => AppSettings.DetectCopyInAllOnBlame = v, "Slower on big repositories."),
            B("Display the author first", () => AppSettings.BlameDisplayAuthorFirst, v => AppSettings.BlameDisplayAuthorFirst = v),
            B("Show the author", () => AppSettings.BlameShowAuthor, v => AppSettings.BlameShowAuthor = v),
            B("Show the author date", () => AppSettings.BlameShowAuthorDate, v => AppSettings.BlameShowAuthorDate = v),
            B("Show the author time", () => AppSettings.BlameShowAuthorTime, v => AppSettings.BlameShowAuthorTime = v),
            B("Show line numbers", () => AppSettings.BlameShowLineNumbers, v => AppSettings.BlameShowLineNumbers = v),
            B("Show the original file path", () => AppSettings.BlameShowOriginalFilePath, v => AppSettings.BlameShowOriginalFilePath = v),
            B("Show the author avatar", () => AppSettings.BlameShowAuthorAvatar, v => AppSettings.BlameShowAuthorAvatar = v),
            NotAppliedNotice());
    }

    private static SettingsPageViewModel Ssh()
    {
        const string openSsh = "OpenSSH";
        const string other = "Other SSH client";
        SettingsPageViewModel page = new(SettingsPageIds.Ssh, "SSH", "Key");
        TextRow path = new("Path to the SSH client", () => AppSettings.SshPath, v => AppSettings.SshPath = v) { Browse = BrowseKind.File };
        ChoiceRow client = new(
            "SSH client",
            [openSsh, other],
            () => string.IsNullOrWhiteSpace(AppSettings.SshPath) ? openSsh : other,
            v =>
            {
                if (v == openSsh)
                {
                    AppSettings.SshPath = string.Empty;
                    path.Value = string.Empty;
                }
            });
        path.IsEnabled = client.Value == other;
        client.Changed += _ => path.IsEnabled = client.Value == other;
        return page.With(
            client,
            path,
            new NoticeRow("OpenSSH (ssh from the PATH) is used when no other client is configured. PuTTY (plink, pageant) is a Windows tool and is not configured here."));
    }
}
