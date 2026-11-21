using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GitExtensions.Desktop.History;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Desktop.ViewModels;

public sealed partial class CommitItemViewModel : ObservableObject
{
    public CommitItemViewModel(GitRevision revision, string refs, IReadOnlyList<RefBadge>? badges = null)
    {
        Revision = revision;
        ShortHash = revision.ObjectId.ToShortString();
        Subject = revision.Subject;
        BodyPreview = BuildBodyPreview(revision);
        Author = revision.Author ?? string.Empty;
        Date = revision.AuthorDate.ToString("g", CultureInfo.CurrentCulture);
        Refs = refs;
        RefBadges = badges ?? [];
        RelativeDate = RelativeDateFormatter.Format(revision.AuthorDate);
        DateToolTip = RelativeDateFormatter.Absolute(revision.AuthorDate);
        IsMerge = revision.ParentIds?.Count > 1;
        Initials = AvatarStyle.Initials(Author);
        AvatarColorIndex = AvatarStyle.ColorIndex(Author);
        IsCurrentHead = RefBadges.Any(badge => badge.IsCurrent);
    }

    /// <summary>The rest of the message after the subject on one line (grey, after the subject in the grid like in the original); empty without a body.</summary>
    public string BodyPreview { get; }

    public GitRevision Revision { get; }

    /// <summary>Graph cell data, set when the commit is added to the history.</summary>
    public GitExtensions.Desktop.History.CommitGraphRow? Graph { get; set; }

    /// <summary>The context menu operations (shared by all rows).</summary>
    public CommitActionsViewModel? Actions { get; set; }

    public ObjectId ObjectId => Revision.ObjectId;

    public string ShortHash { get; }

    public string Subject { get; }

    public string Author { get; }

    public string Date { get; }

    /// <summary>
    ///  Branches and tags pointing at this commit.
    /// </summary>
    public string Refs { get; }

    public bool HasRefs => Refs.Length > 0;

    /// <summary>The labels shown next to the subject (at most a few plus a "…" chip), see <see cref="RefBadgeBuilder"/>.</summary>
    public IReadOnlyList<RefBadge> RefBadges { get; }

    public bool HasRefBadges => RefBadges.Count > 0;

    /// <summary>The commit is on this branch but not on its remote yet (shown with an up arrow, like GitHub Desktop).</summary>
    [ObservableProperty]
    private bool _isUnpushed;

    /// <summary>The first tag on the commit, shown as a chip in the simple history list; <see langword="null"/> when none.</summary>
    public string? TagName => RefBadges.FirstOrDefault(badge => badge.Kind == RefBadgeKind.Tag)?.Name;

    public bool HasTag => TagName is not null;

    /// <summary>"15 days ago"; the absolute date is <see cref="DateToolTip"/>.</summary>
    public string RelativeDate { get; }

    public string DateToolTip { get; }

    public bool IsMerge { get; }

    /// <summary>The node is drawn as a square: merge commits and the tips that carry a branch or tag label, like the original.</summary>
    public bool IsGraphSquare => IsMerge || HasRefBadges;

    /// <summary>The commit the current branch points at.</summary>
    public bool IsCurrentHead { get; }

    /// <summary>True for commits whose author is the configured git user (<c>user.name</c>); shown bold.</summary>
    public bool IsMine { get; init; }

    /// <summary>The commit is not an ancestor of the checked out branch: the row is shown greyed.</summary>
    public bool IsDimmed { get; set; }

    /// <summary>Every other row gets a subtle background tint.</summary>
    public bool IsAlternate { get; init; }

    public string Initials { get; }

    public int AvatarColorIndex { get; }

    public IBrush AvatarBrush => Views.AvatarBrushes.For(AvatarColorIndex);

    /// <summary>The build server's verdict for the commit, if known; the grid's status column stays empty without one.</summary>
    public BuildStatus BuildStatus { get; init; }

    public bool HasBuildStatus => BuildStatus != BuildStatus.None;

    public bool IsBuildSuccess => BuildStatus == BuildStatus.Success;

    public bool IsBuildFailure => BuildStatus == BuildStatus.Failure;

    public bool IsBuildRunning => BuildStatus == BuildStatus.Running;

    /// <summary>What a screen reader announces for the row.</summary>
    public string AccessibleName => HasRefs ? $"{Subject}, by {Author}, {Date}, {ShortHash}, {Refs}" : $"{Subject}, by {Author}, {Date}, {ShortHash}";

    // Everything after the first line of the message, whitespace collapsed, capped so a huge body costs nothing to draw.
    // Commits older than about six months have no body loaded (like in the original), so they show the subject only.
    private static string BuildBodyPreview(GitRevision revision)
    {
        string body = revision.Body ?? string.Empty;
        int firstLineEnd = body.IndexOf('\n');
        if (firstLineEnd < 0 || firstLineEnd + 1 >= body.Length)
        {
            return string.Empty;
        }

        string rest = body[(firstLineEnd + 1)..].Trim();
        if (rest.Length == 0)
        {
            return string.Empty;
        }

        string oneLine = System.Text.RegularExpressions.Regex.Replace(rest.Length > 600 ? rest[..600] : rest, @"\s+", " ");
        return "  " + oneLine;
    }
}

public enum BuildStatus
{
    None,
    Success,
    Failure,
    Running,
}
