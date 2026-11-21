using System.Collections.Specialized;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.ViewModels;

// Marks the commits that are on the current branch but not on the remote yet, like GitHub Desktop's up-arrow badge.
public sealed partial class MainWindowViewModel
{
    /// <summary>More local commits than this are not marked one by one (a branch that was never pushed can be huge).</summary>
    public const int MaxMarkedUnpushedCommits = 500;

    private HashSet<ObjectId> _unpushed = [];
    private int _unpushedGeneration;

    /// <summary>How many commits of the current branch the remote does not have, for the Push button's badge.</summary>
    public int AheadCount => Refs.CurrentTrack?.Ahead ?? 0;

    public bool HasAheadBadge => AheadCount > 0 && SyncAction == SyncAction.Push;

    private void InitializeUnpushed()
    {
        // History loads in batches, added in bulk (a Reset, not an Add with the new items): mark whatever arrived.
        Commits.CollectionChanged += (_, e) =>
        {
            IEnumerable<CommitItemViewModel> arrived = e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null
                ? e.NewItems.OfType<CommitItemViewModel>()
                : Commits;
            foreach (CommitItemViewModel commit in arrived)
            {
                commit.IsUnpushed = _unpushed.Contains(commit.ObjectId);
            }
        };
        Refs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RefsTreeViewModel.CurrentTrack))
            {
                OnPropertyChanged(nameof(AheadCount));
                OnPropertyChanged(nameof(HasAheadBadge));
                _ = UpdateUnpushedAsync();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Branch) or nameof(SyncAction))
            {
                OnPropertyChanged(nameof(HasAheadBadge));
                if (e.PropertyName == nameof(Branch))
                {
                    _ = UpdateUnpushedAsync();
                }
            }
        };
    }

    /// <summary>Reads which commits are not pushed yet and marks them in the history.</summary>
    public async Task UpdateUnpushedAsync()
    {
        if (_module is not { } module)
        {
            return;
        }

        int generation = ++_unpushedGeneration;
        HashSet<ObjectId> unpushed;
        try
        {
            unpushed = await Task.Run(() => ReadUnpushed(module));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not read the unpushed commits: {ex.Message}");
            return;
        }

        if (generation != _unpushedGeneration || !ReferenceEquals(_module, module))
        {
            return;
        }

        _unpushed = unpushed;
        foreach (CommitItemViewModel commit in Commits)
        {
            commit.IsUnpushed = unpushed.Contains(commit.ObjectId);
        }
    }

    // With an upstream: what HEAD has that the upstream does not. Without one (never published): what no remote has,
    // and only when the repository has a remote at all (a local-only repository has nothing "not pushed yet").
    private static HashSet<ObjectId> ReadUnpushed(GitModule module)
    {
        ExecutionResult upstream = module.GitExecutable.Execute("rev-parse --abbrev-ref --symbolic-full-name @{u}", throwOnErrorExit: false);
        string range;
        if (upstream.ExitedSuccessfully)
        {
            range = "@{u}..HEAD";
        }
        else if (module.GitExecutable.Execute("remote", throwOnErrorExit: false).StandardOutput.Trim().Length > 0)
        {
            range = "HEAD --not --remotes";
        }
        else
        {
            return [];
        }

        ExecutionResult result = module.GitExecutable.Execute($"rev-list --max-count={MaxMarkedUnpushedCommits} {range}", throwOnErrorExit: false);
        HashSet<ObjectId> ids = [];
        if (result.ExitedSuccessfully)
        {
            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ObjectId.TryParse(line, out ObjectId id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
