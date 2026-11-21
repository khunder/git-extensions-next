using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.ViewModels;

public sealed class ChangedFileViewModel
{
    public ChangedFileViewModel(GitItemStatus item)
    {
        Item = item;
        Name = item.Name;
        Status = item switch
        {
            { IsUnmerged: true } => "U",
            { IsNew: true } => "A",
            { IsDeleted: true } => "D",
            { IsRenamed: true } => "R",
            { IsCopied: true } => "C",
            _ => "M"
        };
        IsStaged = item.Staged == StagedStatus.Index;
        int slash = item.Name.LastIndexOf('/');
        FileName = slash < 0 ? item.Name : item.Name[(slash + 1)..];
        Directory = slash < 0 ? string.Empty : item.Name[..slash];
        Description = Status switch
        {
            "U" => "Unmerged (conflict)",
            "A" => item.IsTracked ? "Added" : "New (untracked)",
            "D" => "Deleted",
            "R" => "Renamed",
            "C" => "Copied",
            _ => "Modified"
        };
    }

    /// <summary>The icon name for the change: the pen (modified), green plus (added), red minus (deleted), like the original.</summary>
    public string StatusIcon => Status switch
    {
        "A" => "FileAdded",
        "D" => "FileRemoved",
        "R" or "C" => "Renamed",
        "U" => "Unmerged",
        _ => "FileStatusModified"
    };

    /// <summary>The file name without its folder.</summary>
    public string FileName { get; }

    /// <summary>The folder of the file relative to the working directory (empty at the top level).</summary>
    public string Directory { get; }

    /// <summary>The folder with a trailing slash, shown dimmed before the file name (empty at the top level).</summary>
    public string DirectoryPrefix => Directory.Length == 0 ? string.Empty : Directory + "/";

    // The status box of the GitHub Desktop style lists (Styles/FileStatus.axaml).
    public bool IsAdded => Status is "A" or "C";

    public bool IsDeleted => Status == "D";

    public bool IsModified => Status == "M";

    public bool IsRenamed => Status == "R";

    /// <summary>The change kind in words, e.g. for a tooltip.</summary>
    public string Description { get; }

    public GitItemStatus Item { get; }

    /// <summary>The name for lists: renames show as "old → new".</summary>
    public string DisplayName => Item.IsRenamed && !string.IsNullOrEmpty(Item.OldName) ? $"{Item.OldName} → {Name}" : Name;

    public string Name { get; }

    /// <summary>
    ///  Single-letter status, like in <c>git status --short</c>.
    /// </summary>
    public string Status { get; }

    public bool IsStaged { get; }

    /// <summary>The file has an unresolved merge conflict.</summary>
    public bool IsConflicted => Item.IsUnmerged;
}
