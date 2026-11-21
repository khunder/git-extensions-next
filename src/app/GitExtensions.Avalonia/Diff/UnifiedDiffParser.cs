using System.Text.RegularExpressions;

namespace GitExtensions.Desktop.Diff;

/// <summary>
///  Parses unified diff text (as produced by <c>git diff</c>) into classified lines.
/// </summary>
public static partial class UnifiedDiffParser
{
    [GeneratedRegex(@"^@@ -(?<os>\d+)(,(?<oc>\d+))? \+(?<ns>\d+)(,(?<nc>\d+))? @@")]
    private static partial Regex HunkHeader();

    public static IReadOnlyList<DiffLine> Parse(string? diff)
    {
        List<DiffLine> result = [];
        if (string.IsNullOrEmpty(diff))
        {
            return result;
        }

        int oldRemaining = 0;
        int newRemaining = 0;
        int oldLine = 0;
        int newLine = 0;
        int hunkIndex = -1;

        foreach (string line in SplitLines(diff))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) && HunkHeader().Match(line) is { Success: true } match)
            {
                hunkIndex++;
                oldLine = int.Parse(match.Groups["os"].Value);
                newLine = int.Parse(match.Groups["ns"].Value);
                oldRemaining = match.Groups["oc"].Success ? int.Parse(match.Groups["oc"].Value) : 1;
                newRemaining = match.Groups["nc"].Success ? int.Parse(match.Groups["nc"].Value) : 1;
                result.Add(new DiffLine(DiffLineKind.Hunk, line, null, null) { HunkIndex = hunkIndex });
                continue;
            }

            if (line.StartsWith('\\'))
            {
                result.Add(new DiffLine(DiffLineKind.Info, line, null, null) { HunkIndex = hunkIndex });
                continue;
            }

            // Inside a hunk the declared line counts decide what a line is, so "--- foo" removed content is not mistaken for a header.
            bool inHunk = oldRemaining > 0 || newRemaining > 0;
            if (inHunk && line.Length > 0 && line[0] == '+' && newRemaining > 0)
            {
                result.Add(new DiffLine(DiffLineKind.Added, line, null, newLine++) { HunkIndex = hunkIndex });
                newRemaining--;
            }
            else if (inHunk && line.Length > 0 && line[0] == '-' && oldRemaining > 0)
            {
                result.Add(new DiffLine(DiffLineKind.Removed, line, oldLine++, null) { HunkIndex = hunkIndex });
                oldRemaining--;
            }
            else if (inHunk && oldRemaining > 0 && newRemaining > 0)
            {
                result.Add(new DiffLine(DiffLineKind.Context, line, oldLine++, newLine++) { HunkIndex = hunkIndex });
                oldRemaining--;
                newRemaining--;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Header, line, null, null));
            }
        }

        SyntaxHighlighter.Annotate(result);
        return result;
    }

    /// <summary>
    ///  Builds the diff lines of an untracked file: every line is an addition.
    /// </summary>
    public static IReadOnlyList<DiffLine> FromNewFileContent(string fileName, string content)
    {
        List<DiffLine> result = [new DiffLine(DiffLineKind.Header, $"new file: {fileName}", null, null)];
        if (content.Length == 0)
        {
            return result;
        }

        List<string> lines = [.. SplitLines(content)];
        result.Add(new DiffLine(DiffLineKind.Hunk, $"@@ -0,0 +1,{lines.Count} @@", null, null) { HunkIndex = 0 });
        for (int i = 0; i < lines.Count; i++)
        {
            result.Add(new DiffLine(DiffLineKind.Added, "+" + lines[i], null, i + 1) { HunkIndex = 0 });
        }

        SyntaxHighlighter.Annotate(result, fileName);
        return result;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        // Normalize CRLF here so lines render without a trailing carriage return; a final newline does not add an empty line.
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < count; i++)
        {
            yield return lines[i];
        }
    }
}
