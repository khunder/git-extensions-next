using Avalonia.Controls;
using Avalonia.Input;
using GitExtensions.Desktop.History;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;

namespace GitExtensions.Desktop.Views.ContextMenus;

/// <summary>
///  The context menu of a commit in the revision grid, structured like the original application's
///  (RevisionGridControl.Designer.cs): copy, checkout/merge/rebase/reset, branches, tags, commit operations, advanced,
///  compare, navigate, view, and the hosting links. Every entry reuses an existing view-model command; features that do
///  not exist yet are shown disabled with a tooltip.
/// </summary>
public static class RevisionGridContextMenu
{
    public const string ModifyHistoryHelp = "https://git-extensions-documentation.readthedocs.org/en/latest/modify_history.html";

    public static List<Control> Build(MainWindowViewModel vm, CommitItemViewModel commit, IClipboardService? clipboard, bool offerUndo = false)
    {
        IReadOnlyList<RefBadge> refs = vm.RefsOf(commit);
        List<RefBadge> locals = [.. refs.Where(r => r.Kind == RefBadgeKind.LocalBranch)];
        List<RefBadge> others = [.. locals.Where(r => !r.IsCurrent)];
        List<RefBadge> remotes = [.. refs.Where(r => r.Kind == RefBadgeKind.RemoteBranch)];
        List<RefBadge> tags = [.. refs.Where(r => r.Kind == RefBadgeKind.Tag)];
        string? current = vm.Branch;
        string hash = commit.ObjectId.ToString();

        // Right-click selects the row, but the menu may also come from the keyboard or a test: act on this commit.
        void On(Action action)
        {
            vm.SelectedCommit = commit;
            action();
        }

        void Sidebar() => vm.ShowSidebar();

        List<Control> items = [];

        // The simple history offers undoing the newest commit of the branch, like GitHub Desktop: it goes away, its changes stay
        // staged. The full grid keeps the original menu (its Undo last commit is in the Commands menu).
        if (offerUndo && commit.IsCurrentHead && !vm.Bisect.IsBisecting)
        {
            items.Add(MenuKit.Item("Undo commit…", "ResetWorkingDirChanges", () => _ = vm.UndoLastCommitCommand.ExecuteAsync(null),
                tip: "Remove this commit and keep its changes, ready to commit again"));
            items.Add(MenuKit.Sep());
        }

        // Bisect marks (while a bisect runs) come first, like the original.
        if (vm.Bisect.IsBisecting)
        {
            items.Add(MenuKit.Item("Mark revision as bad", "BisectBad", () => On(() => vm.MarkBisect(vm.Bisect.MarkBadCommand))));
            items.Add(MenuKit.Item("Mark revision as good", "BisectGood", () => On(() => vm.MarkBisect(vm.Bisect.MarkGoodCommand))));
            items.Add(MenuKit.Item("Skip revision", "BisectSkip", () => On(() => vm.MarkBisect(vm.Bisect.SkipCommand))));
            items.Add(MenuKit.Item("Stop bisect", "BisectStop", () => vm.Bisect.RequestResetCommand.Execute(null)));
            items.Add(MenuKit.Sep());
        }

        items.Add(MenuKit.Sub("Copy to clipboard", "CopyToClipboard", CopyItems(commit, refs, clipboard)));
        items.Add(MenuKit.Sep());

        // ---- branches ----
        List<Control> checkout = [.. locals.Where(r => !r.IsCurrent).Select(r => MenuKit.Item(r.Name, "BranchLocal", () => _ = vm.CheckoutBranchAsync(r.Name)))];
        checkout.AddRange(remotes.Select(r => MenuKit.Item(r.Name, "BranchRemote", () => CheckoutRemote(vm, r.Name))));
        items.Add(MenuKit.Sub("Checkout branch…", "BranchCheckout", checkout, checkout.Count > 0, "No other branch points at this commit"));

        // Merge and rebase open their dialog with the branch at this commit (or the commit itself) prefilled.
        string mergeTarget = others.Concat(remotes).Select(r => r.Name).FirstOrDefault() ?? hash;
        items.Add(MenuKit.Item("Merge into current branch…", "Merge", () => _ = vm.OpenMergeDialogAsync(mergeTarget), enabled: current is not null, disabledTip: "HEAD is detached: there is no current branch"));
        items.Add(MenuKit.Item("Rebase current branch on…", "Rebase", () => _ = vm.OpenRebaseDialogAsync(mergeTarget), enabled: current is not null, disabledTip: "HEAD is detached: there is no current branch"));

        items.Add(MenuKit.Item("Reset current branch to here…", "ResetCurrentBranchToHere", () => On(vm.RevisionOps.OpenReset)));
        items.Add(MenuKit.Sep());

        items.Add(MenuKit.Item("Create new branch here…", "BranchCreate", () => commit.Actions?.RequestCreateBranchCommand.Execute(commit), "Ctrl+B", enabled: commit.Actions is not null));
        items.Add(MenuKit.Sub(
            "Rename branch…",
            "Renamed",
            locals.Select(r => MenuKit.Item(r.Name, "BranchLocal", () => WithNode(vm, RefNodeKind.LocalBranch, r.Name, node => vm.Refs.RequestRenameCommand.Execute(node), Sidebar))),
            locals.Count > 0,
            "No local branch points at this commit"));
        items.Add(MenuKit.Sub(
            "Delete branch…",
            "BranchDelete",
            others.Select(r => MenuKit.Item(r.Name, "BranchLocal", () => WithNode(vm, RefNodeKind.LocalBranch, r.Name, node => vm.Refs.DeleteBranchCommand.Execute(node), Sidebar))),
            others.Count > 0,
            "No deletable branch points at this commit (the current branch cannot be deleted)"));
        items.Add(MenuKit.Sub(
            "Push branch…",
            "Push",
            locals.Select(r => MenuKit.Item(r.Name, "BranchLocal", () => WithNode(vm, RefNodeKind.LocalBranch, r.Name, node => vm.Refs.PushBranchCommand.Execute(node), null))),
            locals.Count > 0,
            "No local branch points at this commit"));
        items.Add(MenuKit.Item("Reset another branch here…", "ResetAnotherBranchToHere", () => _ = vm.OpenHistoryEditAsync(HistoryEditKind.ResetBranch, commit)));
        items.Add(MenuKit.Sep());

        // ---- tags ----
        items.Add(MenuKit.Item("Create new tag here…", "TagCreate", () => commit.Actions?.RequestCreateTagCommand.Execute(commit), "Ctrl+T", enabled: commit.Actions is not null));
        items.Add(MenuKit.Sub(
            "Delete tag…",
            "TagDelete",
            tags.Select(r => MenuKit.Item(r.Name, "Tag", () => WithNode(vm, RefNodeKind.Tag, r.Name, node => vm.Refs.RequestDeleteTagCommand.Execute(node), Sidebar))),
            tags.Count > 0,
            "No tag points at this commit"));
        items.Add(MenuKit.Sep());

        // ---- commit operations ----
        items.Add(MenuKit.Item("Checkout this commit…", "Checkout", () => On(vm.RevisionOps.OpenCheckoutRevision)));
        items.Add(MenuKit.Item("Revert this commit…", "RevertCommit", () => commit.Actions?.RequestRevertCommand.Execute(commit), enabled: commit.Actions is not null));
        items.Add(MenuKit.Item("Cherry pick this commit…", "CherryPick", () => On(vm.RevisionOps.OpenCherryPick)));
        items.Add(MenuKit.Item("Archive this commit…", "ArchiveRevision", () => On(vm.RevisionOps.OpenArchive)));
        items.Add(MenuKit.Item("Format patch…", "PatchFormat", () => vm.ShowFormatPatchForCommand.Execute(commit)));
        items.Add(MenuKit.Sub("Advanced", "Advanced", Advanced(vm, commit)));
        items.Add(MenuKit.Sep());

        items.Add(MenuKit.Sub("Compare", "Diff", Compare(vm, commit)));
        items.Add(MenuKit.Sep());
        items.Add(MenuKit.Sub("Navigate", "GotoCommit", Navigate(vm, commit)));
        items.Add(MenuKit.Item(
            "Select in left panel",
            "FileTree",
            () => SelectInLeftPanel(vm, refs),
            enabled: refs.Count > 0,
            disabledTip: "No branch or tag points at this commit"));
        items.Add(MenuKit.Sub("View", "AdvancedSettings", View(vm)));
        items.Add(MenuKit.Sep());

        items.Add(MenuKit.Item(
            "Open on GitHub",
            "github",
            () => vm.OpenCommitOnGitHub(commit),
            enabled: vm.GitHubWebUrl is not null,
            disabledTip: "The remote of this repository is not on GitHub"));
        items.Add(MenuKit.Stub("View pull request in a browser", "PullRequest"));
        items.Add(MenuKit.Item(
            "View build report in a browser",
            "Integration",
            enabled: false,
            disabledTip: commit.HasBuildStatus ? MenuKit.NotAvailableTip : "No build status is known for this commit"));
        return MenuKit.Tidy(items);

        static void CheckoutRemote(MainWindowViewModel vm, string name)
        {
            if (vm.Refs.FindNode(RefNodeKind.RemoteBranch, name) is { } node)
            {
                vm.Refs.CheckoutRemoteAsTrackingCommand.Execute(node);
            }
        }
    }

    private static void WithNode(MainWindowViewModel vm, RefNodeKind kind, string name, Action<RefNodeViewModel> action, Action? beforePrompt)
    {
        if (vm.Refs.FindNode(kind, name) is { } node)
        {
            beforePrompt?.Invoke();
            action(node);
        }
        else
        {
            vm.Status = $"'{name}' is hidden by the filter of the left panel: clear the filter first.";
        }
    }

    private static void SelectInLeftPanel(MainWindowViewModel vm, IReadOnlyList<RefBadge> refs)
    {
        foreach (RefBadge badge in refs)
        {
            RefNodeKind kind = badge.Kind switch { RefBadgeKind.Tag => RefNodeKind.Tag, RefBadgeKind.RemoteBranch => RefNodeKind.RemoteBranch, _ => RefNodeKind.LocalBranch };
            if (vm.Refs.FindNode(kind, badge.Name) is { } node)
            {
                vm.ShowSidebar();
                vm.Refs.SelectedNode = node;
                return;
            }
        }
    }

    private static List<Control> CopyItems(CommitItemViewModel commit, IReadOnlyList<RefBadge> refs, IClipboardService? clipboard)
    {
        GitUIPluginInterfaces.GitRevision revision = commit.Revision;
        string author = $"{revision.Author} <{revision.AuthorEmail}>";
        string message = revision.Body ?? revision.Subject;
        List<Control> items = [];
        void Add(string label, string text, string icon, string? gesture = null)
            => items.Add(MenuKit.Item($"{label}:   {Shorten(text.Split('\n')[0], 40)}", icon, () => _ = clipboard?.SetTextAsync(text), gesture, enabled: clipboard is not null));

        string[] branchNames = [.. refs.Where(r => r.Kind != RefBadgeKind.Tag).Select(r => r.Name)];
        string[] tagNames = [.. refs.Where(r => r.Kind == RefBadgeKind.Tag).Select(r => r.Name)];
        if (branchNames.Length > 0)
        {
            items.Add(MenuKit.Section("Branches"));
            items.AddRange(branchNames.Select(name => MenuKit.Item(name, "Branch", () => _ = clipboard?.SetTextAsync(name), enabled: clipboard is not null)));
            items.Add(MenuKit.Sep());
        }

        if (tagNames.Length > 0)
        {
            items.Add(MenuKit.Section("Tags"));
            items.AddRange(tagNames.Select(name => MenuKit.Item(name, "Tag", () => _ = clipboard?.SetTextAsync(name), enabled: clipboard is not null)));
            items.Add(MenuKit.Sep());
        }

        Add("Commit hash", commit.ObjectId.ToString(), "CommitId", "C");
        Add("Message", message, "Message", "M");
        Add("Author", author, "Author", "A");
        Add("Author date", revision.AuthorDate.ToString("g", System.Globalization.CultureInfo.CurrentCulture), "Date", "T");
        Add("Commit date", revision.CommitDate.ToString("g", System.Globalization.CultureInfo.CurrentCulture), "Date", "D");
        return items;
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static List<Control> Advanced(MainWindowViewModel vm, CommitItemViewModel commit)
    {
        bool bare = vm.RepositoryPath.Length == 0;
        void Open(HistoryEditKind kind) => _ = vm.OpenHistoryEditAsync(kind, commit);
        bool hasParent = commit.Revision.HasParent;
        return
        [
            MenuKit.Item("Edit commit", "EditFile", () => Open(HistoryEditKind.Edit), enabled: !bare, tip: "Rebase the current branch and stop at this commit so it can be changed"),
            MenuKit.Item("Edit commit by creating a fixup commit and squash-rebase…", "EditFile", () => Open(HistoryEditKind.FixupAndRebase), enabled: !bare && hasParent, disabledTip: "The commit has no parent", tip: "Commit the staged changes as a fixup of this commit and fold it in with an autosquash rebase"),
            MenuKit.Item("Reword commit", "Message", () => Open(HistoryEditKind.Reword), enabled: !bare, tip: "Change the message of this commit by rebasing"),
            MenuKit.Item("Create a fixup commit…", "Message", () => Open(HistoryEditKind.Fixup), enabled: !bare, tip: "git commit --fixup of the staged changes"),
            MenuKit.Item("Create a squash commit…", "Message", () => Open(HistoryEditKind.Squash), enabled: !bare, tip: "git commit --squash of the staged changes"),
            MenuKit.Item("Create an amend commit…", "Message", () => Open(HistoryEditKind.Amend), enabled: !bare, tip: "git commit --fixup=amend: to change the message of this commit later"),
            MenuKit.Sep(),
            MenuKit.Item("Autosquash fixup commits into this commit's parent…", "Rebase", () => Open(HistoryEditKind.Autosquash), enabled: !bare, tip: "git rebase -i --autosquash without opening the todo list"),
            MenuKit.Item("Interactive rebase editor…", "Rebase", enabled: false, disabledTip: "Needs the interactive rebase editor"),
            MenuKit.Sep(),
            MenuKit.Item("Get help on how to use these features", "GotoManual", () => ShellLauncher.OpenUrl(ModifyHistoryHelp)),
        ];
    }

    private static List<Control> Compare(MainWindowViewModel vm, CommitItemViewModel commit)
    {
        CommitItemViewModel? baseCommit = vm.CompareBase;
        return
        [
            MenuKit.Item("Open selected commits with difftool", "Diff", () => _ = vm.OpenDifftoolAsync(commit), tip: "git difftool of this commit against its parent"),
            MenuKit.Sep(),
            MenuKit.Item("Compare to branch…", "Diff", () => _ = vm.OpenHistoryEditAsync(HistoryEditKind.CompareWithRevision, commit)),
            MenuKit.Item("Compare with current branch", "Diff", () => _ = vm.CompareWithCurrentBranchAsync(commit), enabled: vm.Branch is not null, disabledTip: "HEAD is detached: there is no current branch"),
            MenuKit.Item("Select as BASE to compare", "Diff", () => vm.CompareBase = commit),
            MenuKit.Item(
                baseCommit is null ? "Compare to BASE" : $"Compare to BASE ({baseCommit.ShortHash})",
                "Diff",
                () => vm.CompareToBase(commit),
                enabled: baseCommit is not null && !ReferenceEquals(baseCommit, commit),
                disabledTip: baseCommit is null ? "Select a commit as BASE first" : "This commit is the BASE"),
            MenuKit.Item("Compare to working directory", "Diff", () => vm.CompareWithWorkingDirectory(commit)),
            MenuKit.Item("Compare selected commits", "Diff", enabled: false, disabledTip: "The grid selects one commit at a time: use 'Select as BASE to compare' on the first and 'Compare to BASE' on the second"),
        ];
    }

    private static List<Control> Navigate(MainWindowViewModel vm, CommitItemViewModel commit)
    {
        bool hasParent = commit.Revision.HasParent;
        bool hasChild = vm.Commits.Any(c => c.Revision.ParentIds?.Contains(commit.ObjectId) == true);
        return
        [
            MenuKit.Item("Go to current revision", "GotoCurrentRevision", () => vm.GoToCurrentRevisionCommand.Execute(null)),
            MenuKit.Item("Go to commit…", "GotoCommit", () => vm.ShowGoToCommitCommand.Execute(null), "Ctrl+Shift+G"),
            MenuKit.Sep(),
            MenuKit.Item("Go to child commit", "GoToChildCommit", () => vm.GoToChildCommitCommand.Execute(null), enabled: hasChild, disabledTip: "No child commit in the list"),
            MenuKit.Item("Go to parent commit", "GoToParentCommit", () => vm.GoToParentCommitCommand.Execute(null), enabled: hasParent, disabledTip: "This commit has no parent"),
            MenuKit.Item("Go to first parent commit", "GoToFirstParentCommit", () => vm.GoToParentCommitCommand.Execute(null), enabled: hasParent, disabledTip: "This commit has no parent"),
            MenuKit.Item(
                "Go to last parent commit",
                "GoToLastParentCommit",
                () =>
                {
                    if (commit.Revision.ParentIds is { Count: > 0 } parents)
                    {
                        vm.SelectCommit(parents[^1]);
                    }
                },
                enabled: commit.IsMerge,
                disabledTip: "This commit is not a merge"),
            MenuKit.Item("Go to common ancestor (merge base)", "GoToMergeBaseCommit", () => _ = vm.GoToMergeBaseAsync(commit), tip: "The common ancestor of this commit and the current commit"),
            MenuKit.Sep(),
            MenuKit.Item("Navigate backward", "NavigateBackward", () => vm.NavigateBackCommand.Execute(null), "Alt+Left"),
            MenuKit.Item("Navigate forward", "NavigateForward", () => vm.NavigateForwardCommand.Execute(null), "Alt+Right"),
        ];
    }

    private static List<Control> View(MainWindowViewModel vm)
    {
        bool all = vm.SelectedBranchChoice is null or MainWindowViewModel.AllBranchesChoice;
        bool currentOnly = vm.SelectedBranchChoice == MainWindowViewModel.CurrentBranchChoice;
        RevisionGridColumns columns = vm.Columns;
        return
        [
            MenuKit.Section("Branches"),
            MenuKit.Radio("Show all branches", "BranchFilter", all, () => vm.SelectedBranchChoice = MainWindowViewModel.AllBranchesChoice, "Ctrl+Shift+A", "BranchLocal"),
            MenuKit.Radio("Show current branch only", "BranchFilter", currentOnly, () => vm.SelectedBranchChoice = MainWindowViewModel.CurrentBranchChoice, "Ctrl+Shift+U", "BranchFilter"),
            MenuKit.Radio("Show filtered branches", "BranchFilter", !all && !currentOnly, () => vm.RequestRevisionFilter(), "Ctrl+Shift+T", "BranchFilter"),
            MenuKit.Stub("Show reflog references", "Book", "Ctrl+Shift+L"),
            MenuKit.Sep(),
            MenuKit.Item("Advanced filter…", "EditFilter", vm.RequestRevisionFilter, "Ctrl+I"),
            MenuKit.Sep(),
            MenuKit.Section("Commits"),
            MenuKit.Stub("Draw non relatives gray"),
            MenuKit.Stub("Highlight selected branch", gesture: "Ctrl+Shift+B"),
            MenuKit.Check("Show merge commits", vm.ShowMergeCommits, on => vm.ShowMergeCommits = on),
            MenuKit.Stub("Show artificial commits"),
            MenuKit.Stub("Show stashes"),
            MenuKit.Stub("Show git notes"),
            MenuKit.Sep(),
            MenuKit.Section("Grid labels"),
            MenuKit.Check("Show remote branches", vm.ShowRemoteBranches, on => vm.ShowRemoteBranches = on, "Ctrl+Shift+R"),
            MenuKit.Check("Show tags", vm.ShowTags, on => vm.ShowTags = on, "Ctrl+Alt+T"),
            MenuKit.Stub("Show superproject tags"),
            MenuKit.Stub("Show superproject remote branches"),
            MenuKit.Stub("Show superproject branches"),
            MenuKit.Sep(),
            MenuKit.Section("Grid info"),
            MenuKit.Check("Show author avatar and name column", columns.ShowAuthor, on => columns.ShowAuthor = on),
            MenuKit.Check("Show date column", columns.ShowDate, on => columns.ShowDate = on),
            MenuKit.Check("Show SHA-1 column", columns.ShowHash, on => columns.ShowHash = on),
            MenuKit.Stub("Show build status icon"),
            MenuKit.Stub("Show build status text"),
            MenuKit.Stub("Show commit message body"),
            MenuKit.Stub("Show relative date"),
        ];
    }

    /// <summary>
    ///  The shortcuts of the View entries while the revision grid has the focus (the window's own shortcuts are tried first).
    /// </summary>
    public static bool TryHandleKey(MainWindowViewModel vm, KeyEventArgs e)
    {
        KeyModifiers m = e.KeyModifiers;
        bool ctrlShift = m == (KeyModifiers.Control | KeyModifiers.Shift);
        if (ctrlShift && e.Key == Key.A)
        {
            vm.SelectedBranchChoice = MainWindowViewModel.AllBranchesChoice;
        }
        else if (ctrlShift && e.Key == Key.U)
        {
            vm.SelectedBranchChoice = MainWindowViewModel.CurrentBranchChoice;
        }
        else if (ctrlShift && e.Key == Key.R)
        {
            vm.ShowRemoteBranches = !vm.ShowRemoteBranches;
        }
        else if (m == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.T)
        {
            vm.ShowTags = !vm.ShowTags;
        }
        else if (m == KeyModifiers.Control && e.Key == Key.I)
        {
            vm.RequestRevisionFilter();
        }
        else if (m == KeyModifiers.Control && e.Key == Key.T && vm.SelectedCommit is { Actions: { } actions } selected)
        {
            actions.RequestCreateTagCommand.Execute(selected);
        }
        else
        {
            return false;
        }

        return true;
    }
}
