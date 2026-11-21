using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Desktop.Services;

/// <summary>Where the commit info is shown relative to the revision grid and the tabs.</summary>
public enum CommitInfoLayout
{
    /// <summary>As the "Commit" tab below the revision grid.</summary>
    Below,

    /// <summary>As a separate pane to the left of the tabs.</summary>
    Left,

    /// <summary>As a separate pane to the right of the tabs.</summary>
    Right,
}

/// <summary>The persisted options of the View menu.</summary>
public sealed record ViewOptionsSettings(
    CommitInfoLayout CommitInfoPosition = CommitInfoLayout.Below,
    bool SideBySide = false,
    bool ShowCommitTab = true,
    bool ShowDiffTab = true,
    bool ShowFileTreeTab = true,
    bool ShowConsoleTab = true,
    bool ShowBuildReportTab = false,
    bool ShowRemoteBranches = true,
    bool ShowTags = true,
    bool ShowFilterBar = true,
    bool ShowChangesTab = false,
    bool SimpleMode = false,
    bool SimpleHistoryGraph = false);

/// <summary>Persists the <see cref="ViewOptionsSettings"/> in a small JSON file; the instance can be replaced for tests.</summary>
public sealed class ViewOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public ViewOptionsStore(string? filePath = null)
        => FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitExtensions", "avalonia-view-options.json");

    /// <summary>The store used by the main window.</summary>
    public static ViewOptionsStore Current { get; set; } = new();

    public string FilePath { get; }

    public ViewOptionsSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ViewOptionsSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new ViewOptionsSettings()
                : new ViewOptionsSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new ViewOptionsSettings();
        }
    }

    public void Save(ViewOptionsSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a view option is not worth bothering the user.
        }
    }
}
