using System.ComponentModel;

namespace GitExtensions.Desktop.Diff;

public enum DiffLineKind
{
    /// <summary>File header lines (<c>diff --git</c>, <c>index</c>, <c>---</c>, <c>+++</c>, ...).</summary>
    Header,

    /// <summary>A hunk header (<c>@@ -1,3 +1,4 @@</c>).</summary>
    Hunk,

    Context,
    Added,
    Removed,

    /// <summary>Informational text such as <c>\ No newline at end of file</c>.</summary>
    Info
}

/// <summary>
///  One line of a unified diff. Line numbers are <see langword="null"/> where they do not apply.
///  <see cref="HunkIndex"/> numbers the hunks of the diff (-1 for lines outside any hunk).
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber, int? NewLineNumber) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OldNumber => OldLineNumber?.ToString() ?? string.Empty;

    public string NewNumber => NewLineNumber?.ToString() ?? string.Empty;

    /// <summary>Gets the text to draw: truncated for very long lines, control characters made visible.</summary>
    public string DisplayText => DiffLimits.ToDisplayText(Text);

    public int HunkIndex { get; init; } = -1;

    /// <summary>Gets or sets the syntax colors of <see cref="Text"/>; <see langword="null"/> when the line is not colored.</summary>
    public IReadOnlyList<SyntaxSpan>? Syntax { get; set; }

    /// <summary>Gets a value indicating whether this is an added or removed line (the only lines that can be selected).</summary>
    public bool IsChange => Kind is DiffLineKind.Added or DiffLineKind.Removed;

    /// <summary>Gets a value indicating whether the hunk header row shows the "Stage hunk" button.</summary>
    public bool CanStageHunk { get; init; }

    /// <summary>Gets a value indicating whether the hunk header row shows the "Unstage hunk" button.</summary>
    public bool CanUnstageHunk { get; init; }

    /// <summary>Gets a value indicating whether the hunk header row shows the "Discard hunk" button.</summary>
    public bool CanDiscardHunk { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
