namespace GitCommands.DiffMergeTools;

/// <summary>
/// Suggests a diff/merge tool, the path to its executable and the command git runs, like the "Suggest" buttons of the original settings page.
/// </summary>
public static class DiffMergeToolSuggestion
{
    /// <summary>The tool suggested when nothing else is configured.</summary>
    public const string RecommendedTool = "diffweave";

    /// <summary>
    /// Gets the names of the registered tools of the given type, the recommended one first.
    /// </summary>
    public static IReadOnlyList<string> Tools(DiffMergeToolType toolType)
        => RegisteredDiffMergeTools.All(toolType)
            .OrderBy(name => !string.Equals(name, RecommendedTool, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Suggests a tool: the recommended one when it is installed, otherwise the first installed registered tool,
    /// otherwise the recommended one anyway.
    /// </summary>
    public static string SuggestTool(DiffMergeToolType toolType)
        => Tools(toolType).FirstOrDefault(name => FindExecutable(name) is not null) ?? RecommendedTool;

    /// <summary>
    /// Suggests the path to the executable of <paramref name="toolName"/>, or <see langword="null"/> when it cannot be found.
    /// </summary>
    public static string? SuggestPath(string? toolName)
        => string.IsNullOrWhiteSpace(toolName) ? null : FindExecutable(toolName.Trim());

    /// <summary>
    /// Suggests the command git runs for <paramref name="toolName"/> (the executable followed by its arguments),
    /// or <see langword="null"/> when the tool is unknown.
    /// </summary>
    /// <param name="toolName">The name of the tool.</param>
    /// <param name="toolType">Whether the command is for diffing or merging.</param>
    /// <param name="path">The path to the executable; the suggested path, or the bare executable name, when empty.</param>
    public static string? SuggestCommand(string? toolName, DiffMergeToolType toolType, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(toolName) || RegisteredDiffMergeTools.Get(toolName.Trim()) is not DiffMergeTool tool)
        {
            return null;
        }

        string arguments = toolType == DiffMergeToolType.Diff ? tool.DiffCommand : tool.MergeCommand;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        string executable = string.IsNullOrWhiteSpace(path) ? FindExecutable(tool.Name) ?? tool.ExeFileName : path.Trim().Trim('"');
        return $"\"{executable.ToPosixPath()}\" {arguments}";
    }

    private static string? FindExecutable(string toolName)
    {
        DiffMergeTool? tool = RegisteredDiffMergeTools.Get(toolName);
        string exeFileName = tool?.ExeFileName ?? (OperatingSystem.IsWindows() ? toolName + ".exe" : toolName);

        if (PathUtil.TryFindFullPath(exeFileName, out string? fullPath))
        {
            return fullPath.ToPosixPath();
        }

        if (tool is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            string found = exeFileName.FindInFolders(tool.SearchPaths);
            return string.IsNullOrEmpty(found) ? null : found.ToPosixPath();
        }

        // Places PATH may miss when the app is started from the desktop rather than a shell.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] folders =
        [
            Path.Join(home, ".local", "bin"),
            Path.Join(home, ".local", "opt", toolName, "bin"),
            Path.Join(home, ".local", "opt", toolName),
            "/usr/local/bin",
            "/usr/bin",
            Path.Join("/opt", toolName, "bin"),
            Path.Join("/opt", toolName),
            "/var/lib/flatpak/exports/bin",
            "/snap/bin",
        ];
        return folders.Select(folder => Path.Join(folder, exeFileName)).FirstOrDefault(File.Exists);
    }
}
