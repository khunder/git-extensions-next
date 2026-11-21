namespace GitExtensions.Desktop.Diff;

/// <summary>The kinds of code a diff line is colored by.</summary>
public enum SyntaxKind
{
    Keyword,
    String,
    Comment,
    Number,
    Tag,
    Attribute,
}

/// <summary>A colored range of a diff line's text.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>
///  A small, dependency free syntax highlighter for diffs: keywords, strings, comments, numbers, and the tags and
///  attributes of markup. It is not a parser; it only has to make code easier to read, line by line, like GitHub Desktop.
///  Block comments are followed across lines, separately for the old side (context and removed lines) and the new side
///  (context and added lines) of a hunk.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Lines longer than this are not colored (minified files): it would cost much and help little.</summary>
    public const int MaxHighlightedLineLength = 2000;

    private sealed record Language(
        HashSet<string> Keywords,
        string[] LineComments,
        string? BlockStart = null,
        string? BlockEnd = null,
        string Quotes = "\"'",
        bool Markup = false,
        bool CaseInsensitive = false);

    private static HashSet<string> Words(string words, bool caseInsensitive = false)
        => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly Language CSharp = new(
        Words("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double "
            + "else enum event explicit extern false finally fixed float for foreach get goto if implicit in init int interface internal is lock long "
            + "namespace new null object operator out override params partial private protected public readonly record ref required return sbyte "
            + "sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using "
            + "value var virtual void volatile when where while with yield nameof not and or global file scoped"),
        ["//"], "/*", "*/", "\"'");

    private static readonly Language JavaScript = new(
        Words("abstract any as async await boolean break case catch class const constructor continue debugger declare default delete do else "
            + "enum export extends false finally for from function get if implements import in instanceof interface keyof let module namespace new "
            + "null number of private protected public readonly return set static string super switch this throw true try type typeof undefined "
            + "var void while with yield"),
        ["//"], "/*", "*/", "\"'`");

    private static readonly Language CFamily = new(
        Words("auto bool break case catch char class const constexpr continue default delete do double else enum explicit extends extern false "
            + "final finally float for fn func go if impl implements import include int interface let long match mod mut namespace new nil null "
            + "override package private protected pub public return self short signed sizeof static struct super switch template this throw "
            + "trait true try type typedef typename union unsigned use using var virtual void volatile where while"),
        ["//"], "/*", "*/", "\"'`");

    private static readonly Language Python = new(
        Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda "
            + "None nonlocal not or pass raise return self True try while with yield match case"),
        ["#"], Quotes: "\"'");

    private static readonly Language Shell = new(
        Words("if then else elif fi for while until do done case esac in function return local export echo exit set unset source "
            + "param begin process end foreach switch try catch finally throw"),
        ["#"], Quotes: "\"'", CaseInsensitive: true);

    private static readonly Language Json = new(Words("true false null"), [], Quotes: "\"");

    private static readonly Language Yaml = new(Words("true false null yes no on off"), ["#"], Quotes: "\"'");

    private static readonly Language Sql = new(
        Words("select from where and or not insert into values update set delete create table alter drop index join left right inner outer on "
            + "group by order having limit as distinct union all null is in like between case when then else end primary key foreign references "
            + "with returning exists", caseInsensitive: true),
        ["--"], "/*", "*/", "'", CaseInsensitive: true);

    private static readonly Language Css = new(Words("important media import from to"), [], "/*", "*/", "\"'");

    private static readonly Language Markup = new([], [], "<!--", "-->", "\"'", Markup: true);

    private static readonly Dictionary<string, Language> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = CSharp, [".csx"] = CSharp,
        [".js"] = JavaScript, [".jsx"] = JavaScript, [".mjs"] = JavaScript, [".cjs"] = JavaScript, [".ts"] = JavaScript, [".tsx"] = JavaScript,
        [".java"] = CFamily, [".kt"] = CFamily, [".c"] = CFamily, [".h"] = CFamily, [".cpp"] = CFamily, [".hpp"] = CFamily, [".cc"] = CFamily,
        [".go"] = CFamily, [".rs"] = CFamily, [".swift"] = CFamily, [".php"] = CFamily, [".dart"] = CFamily, [".scala"] = CFamily,
        [".py"] = Python, [".pyw"] = Python,
        [".sh"] = Shell, [".bash"] = Shell, [".zsh"] = Shell, [".ps1"] = Shell, [".psm1"] = Shell,
        [".json"] = Json, [".jsonc"] = CSharp with { Keywords = Json.Keywords, Quotes = "\"" },
        [".yml"] = Yaml, [".yaml"] = Yaml, [".toml"] = Yaml,
        [".sql"] = Sql,
        [".css"] = Css, [".scss"] = Css with { LineComments = ["//"] }, [".less"] = Css with { LineComments = ["//"] },
        [".xml"] = Markup, [".xaml"] = Markup, [".axaml"] = Markup, [".html"] = Markup, [".htm"] = Markup, [".svg"] = Markup,
        [".csproj"] = Markup, [".props"] = Markup, [".targets"] = Markup, [".resx"] = Markup, [".config"] = Markup, [".slnx"] = Markup,
        [".vue"] = Markup, [".svelte"] = Markup,
    };

    /// <summary>True when lines of <paramref name="path"/> can be colored.</summary>
    public static bool IsSupported(string? path) => FindLanguage(path) is not null;

    /// <summary>
    ///  Colors the code lines of a parsed diff in place. The file (and so the language) is read from the diff's header
    ///  lines; a diff of several files switches language at each file.
    /// </summary>
    public static void Annotate(IReadOnlyList<DiffLine> lines, string? path = null)
    {
        Language? language = FindLanguage(path);
        bool oldInBlock = false;
        bool newInBlock = false;
        foreach (DiffLine line in lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Header:
                    if (PathFromHeader(line.Text) is { } headerPath)
                    {
                        language = FindLanguage(headerPath);
                        oldInBlock = newInBlock = false;
                    }

                    break;
                case DiffLineKind.Hunk:
                    // A hunk starts somewhere in the file: assume it is not inside a comment.
                    oldInBlock = newInBlock = false;
                    break;
                case DiffLineKind.Added when language is not null:
                    line.Syntax = Tokenize(line.Text, 1, language, ref newInBlock);
                    break;
                case DiffLineKind.Removed when language is not null:
                    line.Syntax = Tokenize(line.Text, 1, language, ref oldInBlock);
                    break;
                case DiffLineKind.Context when language is not null:
                    bool before = oldInBlock;
                    line.Syntax = Tokenize(line.Text, 1, language, ref oldInBlock);
                    if (before == newInBlock)
                    {
                        newInBlock = oldInBlock;
                    }
                    else
                    {
                        _ = Tokenize(line.Text, 1, language, ref newInBlock);
                    }

                    break;
            }
        }
    }

    /// <summary>Colors one line of code (used by tests and by views that show plain file content).</summary>
    public static IReadOnlyList<SyntaxSpan> Tokenize(string text, string path)
    {
        bool inBlock = false;
        return FindLanguage(path) is { } language ? Tokenize(text, 0, language, ref inBlock) : [];
    }

    private static Language? FindLanguage(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string name = Path.GetFileName(path.Trim().Trim('"'));
        if (name is "Dockerfile" or "Makefile")
        {
            return Shell;
        }

        return ByExtension.GetValueOrDefault(Path.GetExtension(name));
    }

    private static string? PathFromHeader(string header)
    {
        if (header.StartsWith("+++ ", StringComparison.Ordinal))
        {
            string path = header[4..];
            return path == "/dev/null" ? null : StripSidePrefix(path);
        }

        if (header.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            int newSide = header.LastIndexOf(" b/", StringComparison.Ordinal);
            return newSide < 0 ? null : header[(newSide + 3)..];
        }

        if (header.StartsWith("new file: ", StringComparison.Ordinal))
        {
            return header["new file: ".Length..];
        }

        return null;

        static string StripSidePrefix(string path) => path.Length > 2 && path[1] == '/' && path[0] is 'a' or 'b' ? path[2..] : path;
    }

    private static IReadOnlyList<SyntaxSpan> Tokenize(string text, int start, Language language, ref bool inBlock)
    {
        if (text.Length > MaxHighlightedLineLength || start >= text.Length)
        {
            return [];
        }

        List<SyntaxSpan> spans = [];
        int i = start;
        bool inTag = false;
        while (i < text.Length)
        {
            if (inBlock)
            {
                int end = text.IndexOf(language.BlockEnd!, i, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + language.BlockEnd!.Length;
                spans.Add(new SyntaxSpan(i, stop - i, SyntaxKind.Comment));
                inBlock = end < 0;
                i = stop;
                continue;
            }

            char c = text[i];
            if (language.BlockStart is { } blockStart && Matches(text, i, blockStart))
            {
                // One span up to the end of the comment, or of the line when it goes on.
                int end = text.IndexOf(language.BlockEnd!, i + blockStart.Length, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + language.BlockEnd!.Length;
                spans.Add(new SyntaxSpan(i, stop - i, SyntaxKind.Comment));
                inBlock = end < 0;
                i = stop;
                continue;
            }

            if (language.LineComments.FirstOrDefault(comment => Matches(text, i, comment)) is not null)
            {
                spans.Add(new SyntaxSpan(i, text.Length - i, SyntaxKind.Comment));
                break;
            }

            if (language.Markup)
            {
                if (c == '<')
                {
                    int nameStart = i + 1 < text.Length && text[i + 1] is '/' or '?' or '!' ? i + 2 : i + 1;
                    int nameEnd = ScanName(text, nameStart);
                    if (nameEnd > nameStart)
                    {
                        spans.Add(new SyntaxSpan(nameStart, nameEnd - nameStart, SyntaxKind.Tag));
                        inTag = true;
                        i = nameEnd;
                        continue;
                    }
                }
                else if (c == '>')
                {
                    inTag = false;
                }
                else if (inTag && IsNameStart(c))
                {
                    int nameEnd = ScanName(text, i);
                    spans.Add(new SyntaxSpan(i, nameEnd - i, SyntaxKind.Attribute));
                    i = nameEnd;
                    continue;
                }

                if (inTag && language.Quotes.Contains(c, StringComparison.Ordinal))
                {
                    i = ScanString(text, i, spans);
                    continue;
                }

                i++;
                continue;
            }

            if (language.Quotes.Contains(c, StringComparison.Ordinal))
            {
                i = ScanString(text, i, spans);
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsNamePart(text[i - 1])))
            {
                int end = i + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_'))
                {
                    end++;
                }

                spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.Number));
                i = end;
                continue;
            }

            if (IsNameStart(c))
            {
                int end = i + 1;
                while (end < text.Length && IsNamePart(text[end]))
                {
                    end++;
                }

                if (language.Keywords.Contains(text[i..end]))
                {
                    spans.Add(new SyntaxSpan(i, end - i, SyntaxKind.Keyword));
                }

                i = end;
                continue;
            }

            i++;
        }

        return spans;
    }

    // A string up to its closing quote (backslash escapes skipped) or the end of the line.
    private static int ScanString(string text, int start, List<SyntaxSpan> spans)
    {
        char quote = text[start];
        int end = start + 1;
        while (end < text.Length && text[end] != quote)
        {
            end += text[end] == '\\' ? 2 : 1;
        }

        end = Math.Min(end + 1, text.Length);
        spans.Add(new SyntaxSpan(start, end - start, SyntaxKind.String));
        return end;
    }

    private static int ScanName(string text, int start)
    {
        int end = start;
        while (end < text.Length && (IsNamePart(text[end]) || text[end] is ':' or '.' or '-'))
        {
            end++;
        }

        return end;
    }

    private static bool Matches(string text, int index, string token)
        => token.Length > 0 && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static bool IsNameStart(char c) => char.IsLetter(c) || c is '_' or '$' or '@';

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
