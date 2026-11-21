using GitExtensions.Desktop.Diff;

namespace GitExtensionsDesktopTests;

[TestFixture]
public class SyntaxHighlighterTests
{
    private static List<(string Text, SyntaxKind Kind)> Colored(string text, string path)
        => [.. SyntaxHighlighter.Tokenize(text, path).Select(span => (text.Substring(span.Start, span.Length), span.Kind))];

    [Test]
    public void CSharp_keywords_strings_numbers_and_comments_are_colored()
    {
        Colored("public int Count = 42; // the \"answer\"", "a.cs").Should().Equal(
            ("public", SyntaxKind.Keyword),
            ("int", SyntaxKind.Keyword),
            ("42", SyntaxKind.Number),
            ("// the \"answer\"", SyntaxKind.Comment));

        Colored("string s = \"a \\\" b\";", "a.cs").Should().Equal(
            ("string", SyntaxKind.Keyword),
            ("\"a \\\" b\"", SyntaxKind.String));
    }

    [Test]
    public void Identifiers_that_contain_keywords_or_digits_are_not_colored()
    {
        Colored("publicity = item2 + intValue;", "a.cs").Should().BeEmpty();
    }

    [Test]
    public void Markup_tags_attributes_and_values_are_colored()
    {
        Colored("<Button Content=\"Stage\" /> <!-- note -->", "View.axaml").Should().Equal(
            ("Button", SyntaxKind.Tag),
            ("Content", SyntaxKind.Attribute),
            ("\"Stage\"", SyntaxKind.String),
            ("<!-- note -->", SyntaxKind.Comment));
    }

    [Test]
    public void Unknown_file_types_are_not_colored()
    {
        Colored("public int x;", "notes.txt").Should().BeEmpty();
        SyntaxHighlighter.IsSupported("README").Should().BeFalse();
        SyntaxHighlighter.IsSupported("src/app.ts").Should().BeTrue();
    }

    [Test]
    public void A_diff_is_colored_by_its_file_and_block_comments_span_lines_per_side()
    {
        IReadOnlyList<DiffLine> lines = UnifiedDiffParser.Parse(
            """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,3 +1,3 @@
             /* start
            -old line
            +new line */ return;
             end */
            """);

        DiffLine context = lines.Single(line => line.Text == " /* start");
        DiffLine removed = lines.Single(line => line.Kind == DiffLineKind.Removed);
        DiffLine added = lines.Single(line => line.Kind == DiffLineKind.Added);

        context.Syntax.Should().ContainSingle().Which.Kind.Should().Be(SyntaxKind.Comment);
        removed.Syntax.Should().ContainSingle().Which.Kind.Should().Be(SyntaxKind.Comment);

        // On the new side the comment closes on the added line, so "return" is code again.
        added.Syntax!.Select(span => added.Text.Substring(span.Start, span.Length)).Should().Equal("new line */", "return");
        lines.Where(line => line.Kind == DiffLineKind.Header).Should().OnlyContain(line => line.Syntax == null);
    }

    [Test]
    public void An_untracked_file_is_colored_by_its_name()
    {
        IReadOnlyList<DiffLine> lines = UnifiedDiffParser.FromNewFileContent("script.py", "def run():\n    return None\n");

        lines.Single(line => line.NewLineNumber == 1).Syntax.Should().ContainSingle().Which.Kind.Should().Be(SyntaxKind.Keyword);
        lines.Single(line => line.NewLineNumber == 2).Syntax!.Select(span => span.Kind).Should().Equal(SyntaxKind.Keyword, SyntaxKind.Keyword);
    }
}
