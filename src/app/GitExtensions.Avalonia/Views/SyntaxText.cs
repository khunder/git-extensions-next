using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using GitExtensions.Desktop.Diff;

namespace GitExtensions.Desktop.Views;

/// <summary>
///  Shows a diff line in a <see cref="TextBlock"/> with syntax colors: <c>views:SyntaxText.Line="{Binding}"</c> in place
///  of <c>Text="{Binding DisplayText}"</c>. Lines without colors (or shown shortened) keep plain text, which is cheaper.
///  The colors are theme resources (<c>SyntaxKeywordForeground</c>, ...), so a theme switch re-colors the diff live.
/// </summary>
public static class SyntaxText
{
    public static readonly AttachedProperty<DiffLine?> LineProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, DiffLine?>("Line", typeof(SyntaxText));

    static SyntaxText()
    {
        LineProperty.Changed.AddClassHandler<TextBlock>((textBlock, e) => Apply(textBlock, e.GetNewValue<DiffLine?>()));
    }

    public static DiffLine? GetLine(TextBlock element) => element.GetValue(LineProperty);

    public static void SetLine(TextBlock element, DiffLine? value) => element.SetValue(LineProperty, value);

    private static string ResourceKey(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Keyword => "SyntaxKeywordForeground",
        SyntaxKind.String => "SyntaxStringForeground",
        SyntaxKind.Comment => "SyntaxCommentForeground",
        SyntaxKind.Number => "SyntaxNumberForeground",
        SyntaxKind.Tag => "SyntaxTagForeground",
        _ => "SyntaxAttributeForeground",
    };

    private static void Apply(TextBlock textBlock, DiffLine? line)
    {
        textBlock.Inlines?.Clear();
        if (line is null)
        {
            textBlock.Text = null;
            return;
        }

        string display = line.DisplayText;

        // A shortened line or one with control characters made visible no longer matches the colored ranges.
        if (line.Syntax is not { Count: > 0 } spans || !ReferenceEquals(display, line.Text))
        {
            textBlock.Text = display;
            return;
        }

        textBlock.Text = null;
        InlineCollection inlines = textBlock.Inlines ??= [];
        int position = 0;
        foreach (SyntaxSpan span in spans)
        {
            if (span.Start < position || span.Start + span.Length > display.Length)
            {
                continue;
            }

            if (span.Start > position)
            {
                inlines.Add(new Run(display[position..span.Start]));
            }

            Run run = new(display.Substring(span.Start, span.Length));
            run.Bind(TextElement.ForegroundProperty, textBlock.GetResourceObservable(ResourceKey(span.Kind)));
            if (span.Kind == SyntaxKind.Comment)
            {
                run.FontStyle = Avalonia.Media.FontStyle.Italic;
            }

            inlines.Add(run);
            position = span.Start + span.Length;
        }

        if (position < display.Length)
        {
            inlines.Add(new Run(display[position..]));
        }
    }
}
