using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>A repository in the simple mode's repository list.</summary>
public sealed record RepositoryListItem(string Name, string Path, bool IsCurrent);

/// <summary>A titled group of the repository list: "Recent", a GitHub owner, or "Other".</summary>
public sealed record RepositoryListGroup(string Title, IReadOnlyList<RepositoryListItem> Items);

// The "Current repository" drop-down of the simple mode, like GitHub Desktop's: a filter, an Add menu, the most recent
// repositories, then every known repository grouped by its GitHub owner (read from the repository's own config file).
public sealed partial class MainWindowViewModel
{
    /// <summary>How many repositories the "Recent" group shows, like GitHub Desktop.</summary>
    public const int RecentRepositoryCount = 3;

    // Every known repository with its group, read once per opening of the drop-down; filtering only re-groups it.
    private List<(RepositoryListItem Item, string Group)> _knownRepositories = [];

    [ObservableProperty]
    private string _repositoryFilter = string.Empty;

    public ObservableCollection<RepositoryListGroup> RepositoryGroups { get; } = [];

    public bool HasNoRepositoryMatches => RepositoryGroups.Count == 0;

    partial void OnRepositoryFilterChanged(string value) => BuildRepositoryGroups();

    /// <summary>Reads the known repositories (the recent list) and their owners, off the UI thread.</summary>
    public async Task LoadRepositoryListAsync()
    {
        await Recent.LoadAsync();
        string? current = _module?.WorkingDir;
        List<string> paths = [.. Recent.Items.Select(item => item.Path)];
        _knownRepositories = await Task.Run(() => paths
            .Select(path => (new RepositoryListItem(NameOf(path), path, SamePath(path, current)), OwnerOf(path)))
            .ToList());
        BuildRepositoryGroups();
    }

    [RelayCommand]
    private async Task OpenRepositoryFromListAsync(RepositoryListItem? item)
    {
        RepositoryFilter = string.Empty;
        if (item is not null && !item.IsCurrent)
        {
            await OpenAsync(item.Path);
        }
    }

    private void BuildRepositoryGroups()
    {
        string filter = RepositoryFilter.Trim();
        List<RepositoryListItem> matches = [.. _knownRepositories
            .Select(known => known.Item)
            .Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        RepositoryGroups.Clear();
        if (filter.Length == 0 && matches.Where(item => !item.IsCurrent).Take(RecentRepositoryCount).ToList() is { Count: > 0 } recent)
        {
            RepositoryGroups.Add(new RepositoryListGroup("Recent", recent));
        }

        foreach (IGrouping<string, (RepositoryListItem Item, string Group)> group in _knownRepositories
                     .Where(known => matches.Contains(known.Item))
                     .GroupBy(known => known.Group)
                     .OrderBy(group => group.Key == OtherGroup ? 1 : 0)
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            RepositoryGroups.Add(new RepositoryListGroup(
                group.Key,
                [.. group.Select(known => known.Item).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)]));
        }

        OnPropertyChanged(nameof(HasNoRepositoryMatches));
    }

    private const string OtherGroup = "Other";

    private static string NameOf(string path)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private static bool SamePath(string path, string? other)
        => other is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(path.Replace('\\', '/')),
            Path.TrimEndingDirectorySeparator(other.Replace('\\', '/')),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // The GitHub owner of the repository's remote (origin first), like GitHub Desktop's groups; "Other" for the rest.
    private static string OwnerOf(string path)
    {
        try
        {
            string config = Path.Combine(path, ".git", "config");
            if (File.Exists(config)
                && GitHubRemote.WebUrlFromConfig(File.ReadAllText(config)) is { } url
                && url.Split('/', StringSplitOptions.RemoveEmptyEntries) is [.., var owner, _])
            {
                return owner;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return OtherGroup;
    }
}
