using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GitCommands;
using GitExtensions.Desktop.Revision;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  Details of the commit selected in the history: metadata, full message and the files it changed.
/// </summary>
/// <summary>A parent or child commit shown as a link in the commit tab.</summary>
public sealed record CommitLink(ObjectId Id)
{
    public string ShortHash => Id.ToShortString();
}

public sealed partial class CommitDetailsViewModel : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private int _runningExtras;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasCommit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _error;

    [ObservableProperty]
    private string _hash = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _date = string.Empty;

    [ObservableProperty]
    private string _parents = string.Empty;

    [ObservableProperty]
    private string _refs = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageBody), nameof(HasMessageBody))]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _filesHeader = string.Empty;

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private string _authorEmail = string.Empty;

    [ObservableProperty]
    private string _initials = string.Empty;

    [ObservableProperty]
    private Avalonia.Media.IBrush? _avatarBrush;

    [ObservableProperty]
    private string _authorDateText = string.Empty;

    [ObservableProperty]
    private string _commitDateText = string.Empty;

    [ObservableProperty]
    private string _parentsLabel = "Parent:";

    [ObservableProperty]
    private string _childrenLabel = "Child:";

    [ObservableProperty]
    private IReadOnlyList<MessageLink> _relatedLinks = [];

    [ObservableProperty]
    private string _branchesText = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescribe))]
    private string _describeTag = string.Empty;

    [ObservableProperty]
    private string _describeSuffix = string.Empty;

    /// <summary>The commit the nearest tag points at (for the link), when known.</summary>
    public ObjectId? DescribeTagCommit { get; private set; }

    /// <summary>Gets a value indicating whether the branches, tags and nearest tag are still being looked up.</summary>
    public bool IsLoadingExtras => _runningExtras > 0;

    /// <summary>The message without its first line (the subject), for a header that shows the subject separately.</summary>
    public string MessageBody => Message.IndexOf('\n') is int newLine and >= 0 ? Message[(newLine + 1)..].Trim() : string.Empty;

    public bool HasMessageBody => MessageBody.Length > 0;

    public bool HasDescribe => DescribeTag.Length > 0;

    public bool HasParentLinks => ParentLinks.Count > 0;

    public bool HasChildLinks => ChildLinks.Count > 0;

    public bool HasRelatedLinks => RelatedLinks.Count > 0;

    public bool HasAuthorEmail => AuthorEmail.Length > 0;

    /// <summary>The mailto address of the author, or <see langword="null"/>.</summary>
    public string? AuthorMailto => HasAuthorEmail ? $"mailto:{AuthorEmail}" : null;

    /// <summary>The parents as clickable links.</summary>
    public RangeObservableCollection<CommitLink> ParentLinks { get; } = [];

    /// <summary>The children (commits in the loaded history having this one as a parent) as clickable links.</summary>
    public RangeObservableCollection<CommitLink> ChildLinks { get; } = [];

    /// <summary>
    ///  True when there is nothing to show (no selection).
    /// </summary>
    public bool IsEmpty => !HasCommit && !IsLoading && Error is null;

    public ObservableCollection<ChangedFileViewModel> Files { get; } = [];

    /// <summary>
    ///  Clears the pane and cancels any load in flight.
    /// </summary>
    public async Task ClearAsync()
    {
        await CancelCurrentAsync();
        Reset();
        IsLoading = false;
    }

    /// <summary>
    ///  Loads the details of <paramref name="id"/>; a load started earlier is cancelled.
    /// </summary>
    public async Task LoadAsync(GitModule module, ObjectId id, string refs, IReadOnlyList<ObjectId>? children = null)
    {
        await CancelCurrentAsync();
        CancellationTokenSource cancellation = new();
        _cancellation = cancellation;
        CancellationToken cancellationToken = cancellation.Token;

        Reset();
        IsLoading = true;

        try
        {
            // The git calls block, so keep them off the UI thread.
            (GitRevision revision, IReadOnlyList<GitItemStatus> files) = await Task.Run(
                () =>
                {
                    GitRevision revision = module.GetRevision(id);
                    cancellationToken.ThrowIfCancellationRequested();
                    return (revision, GetChangedFiles(module, revision, cancellationToken));
                },
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            Hash = revision.ObjectId.ToString();
            Author = string.IsNullOrEmpty(revision.AuthorEmail) ? revision.Author ?? string.Empty : $"{revision.Author} <{revision.AuthorEmail}>";
            Date = revision.AuthorDate.ToString("f", CultureInfo.CurrentCulture);
            Parents = revision.HasParent ? string.Join("  ", revision.ParentIds!.Select(parentId => parentId.ToShortString())) : "(root commit)";
            Refs = refs;
            Message = (revision.Body ?? revision.Subject).TrimEnd();

            AuthorName = revision.Author ?? string.Empty;
            AuthorEmail = revision.AuthorEmail ?? string.Empty;
            Initials = CommitAvatar.GetInitials(AuthorName, AuthorEmail);
            AvatarBrush = CommitAvatar.GetBrush(string.IsNullOrEmpty(AuthorEmail) ? AuthorName : AuthorEmail);
            DateTime now = DateTime.Now;
            AuthorDateText = RelativeTime.FormatWithDate(now, revision.AuthorDate);
            CommitDateText = RelativeTime.FormatWithDate(now, revision.CommitDate);
            ParentLinks.ReplaceAll((revision.ParentIds ?? []).Select(parentId => new CommitLink(parentId)));
            ChildLinks.ReplaceAll((children ?? []).Select(childId => new CommitLink(childId)));
            ParentsLabel = ParentLinks.Count == 1 ? "Parent:" : "Parents:";
            ChildrenLabel = ChildLinks.Count == 1 ? "Child:" : "Children:";
            OnPropertyChanged(nameof(HasParentLinks));
            OnPropertyChanged(nameof(HasChildLinks));
            OnPropertyChanged(nameof(HasAuthorEmail));
            OnPropertyChanged(nameof(AuthorMailto));
            BranchesText = TagsText = "(…)";
            RelatedLinks = MessageLinker.Find(Message);
            OnPropertyChanged(nameof(HasRelatedLinks));

            foreach (GitItemStatus file in files)
            {
                if (file.IsStatusOnly)
                {
                    // An error entry (not a real file) from the git call.
                    Error = file.ErrorMessage ?? file.Name;
                    continue;
                }

                Files.Add(new ChangedFileViewModel(file));
            }

            FilesHeader = Files.Count == 1 ? "1 file changed" : $"{Files.Count} files changed";
            HasCommit = true;
            IsLoading = false;
            _ = LoadNoteAsync(module.WorkingDir, revision.ObjectId);
            _ = LoadExtrasAsync(module, revision.ObjectId, Message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one; it owns the state now.
        }
        catch (Exception ex)
        {
            Reset();
            Error = $"Failed to load commit: {ex.Message}";
            IsLoading = false;
        }
    }

    // The branches/tags containing the commit and the nearest tag can take long in a big repository: they fill in later.
    // Started from the UI thread; the git calls run on the thread pool and the results are applied back here.
    private async Task LoadExtrasAsync(GitModule module, ObjectId id, string message, CancellationToken cancellationToken)
    {
        _runningExtras++;
        try
        {
            await Task.Delay(120, cancellationToken);
            string sha = id.ToString();
            Task<IReadOnlyList<string>> branches = Task.Run(() => RevisionGit.GetContainingBranches(module, sha, cancellationToken), cancellationToken);
            Task<IReadOnlyList<string>> tags = Task.Run(() => RevisionGit.GetContainingTags(module, sha, cancellationToken), cancellationToken);
            Task<(DescribeParser.Description? Description, ObjectId? Commit)> describe = Task.Run(
                () =>
                {
                    DescribeParser.Description? description = RevisionGit.Describe(module, sha, cancellationToken);
                    return (description, description is { } d ? RevisionGit.GetTagCommit(module, d.Tag, cancellationToken) : null);
                },
                cancellationToken);
            Task<string?> webUrl = Task.Run(() => RevisionGit.GetRepositoryWebUrl(module, cancellationToken), cancellationToken);

            IReadOnlyList<string> branchNames = await branches;
            cancellationToken.ThrowIfCancellationRequested();
            BranchesText = branchNames.Count == 0 ? "Contained in no branch" : $"Contained in branches: {RefListParser.Join(branchNames)}";

            IReadOnlyList<string> tagNames = await tags;
            cancellationToken.ThrowIfCancellationRequested();
            TagsText = tagNames.Count == 0 ? "Contained in no tag" : $"Contained in tags: {RefListParser.Join(tagNames)}";

            (DescribeParser.Description? description, ObjectId? tagCommit) = await describe;
            cancellationToken.ThrowIfCancellationRequested();
            DescribeTagCommit = tagCommit;
            DescribeSuffix = description is { } found ? (found.Commits == 1 ? " + 1 commit" : $" + {found.Commits} commits") : string.Empty;
            DescribeTag = description?.Tag ?? string.Empty;

            string? repositoryWebUrl = await webUrl;
            cancellationToken.ThrowIfCancellationRequested();
            List<MessageLink> links = [];
            if (MessageLinker.GetCommitLink(repositoryWebUrl, id.ToString()) is { } commitLink)
            {
                links.Add(commitLink);
            }

            links.AddRange(MessageLinker.Find(message, repositoryWebUrl));
            RelatedLinks = links;
            OnPropertyChanged(nameof(HasRelatedLinks));
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this one.
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                BranchesText = TagsText = string.Empty;
            }
        }
        finally
        {
            _runningExtras--;
        }
    }

    internal static IReadOnlyList<GitItemStatus> GetChangedFiles(GitModule module, GitRevision revision, CancellationToken cancellationToken)
    {
        if (revision.HasParent)
        {
            return module.GetDiffFilesWithUntracked(revision.FirstParentId.ToString(), revision.ObjectId.ToString(), StagedStatus.None, noCache: true, cancellationToken: cancellationToken);
        }

        // A root commit has nothing to diff against (git diff cannot name the empty tree), so list what it added.
        ExecutionResult result = module.GitExecutable.Execute(
            new GitArgumentBuilder("diff-tree") { "--root", "--no-commit-id", "-r", "--raw", "-z", "--find-renames", revision.ObjectId.ToString() },
            throwOnErrorExit: false,
            cancellationToken: cancellationToken);

        // "-z" raw output is ":<modes> <ids> A\0path\0..."; every file of a root commit is an addition, so only the paths matter.
        return result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !entry.StartsWith(':'))
            .Select(path => new GitItemStatus(path) { IsNew = true })
            .ToList();
    }

    private async Task CancelCurrentAsync()
    {
        CancellationTokenSource? previous = _cancellation;
        _cancellation = null;
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }
    }

    private void Reset()
    {
        HasCommit = false;
        Error = null;
        Hash = Author = Date = Parents = Refs = Message = FilesHeader = string.Empty;
        AuthorName = AuthorEmail = Initials = AuthorDateText = CommitDateText = BranchesText = TagsText = DescribeTag = DescribeSuffix = string.Empty;
        DescribeTagCommit = null;
        AvatarBrush = null;
        RelatedLinks = [];
        ParentLinks.Clear();
        ChildLinks.Clear();
        Files.Clear();
        ResetNote();
    }
}
