namespace GitCommands.DiffMergeTools;

/// <summary>
/// DiffWeave, a cross-platform diff and three-way merge tool with a KDiff3-compatible command line.
/// </summary>
internal sealed class DiffWeave : DiffMergeTool
{
    /// <inheritdoc />
    public override string DiffCommand => "--L1 \"$LOCAL\" --L2 \"$REMOTE\" \"$LOCAL\" \"$REMOTE\"";

    /// <inheritdoc />
    public override string ExeFileName => OperatingSystem.IsWindows() ? "diffweave.exe" : "diffweave";

    /// <inheritdoc />
    /// <remarks>
    /// --auto writes the result and exits without a window when every change merges cleanly;
    /// otherwise the window opens on the first unresolved conflict.
    /// </remarks>
    public override string MergeCommand => "--auto --L1 \"$MERGED (Base)\" --L2 \"$MERGED (Local)\" --L3 \"$MERGED (Remote)\" -o \"$MERGED\" \"$BASE\" \"$LOCAL\" \"$REMOTE\"";

    /// <inheritdoc />
    public override string Name => "diffweave";

    /// <inheritdoc />
    public override IEnumerable<string> SearchPaths => new[]
    {
        @"DiffWeave\",
    };
}
