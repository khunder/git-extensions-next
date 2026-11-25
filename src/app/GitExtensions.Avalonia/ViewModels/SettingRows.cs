using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  One line of a settings page. Rows read their value on <see cref="Load"/> and write it back on <see cref="SaveAsync"/>;
///  rows that are immediate write on every edit (the original saves instantly too).
/// </summary>
public abstract partial class SettingRow : ObservableObject
{
    [ObservableProperty]
    private bool _isMatch;

    [ObservableProperty]
    private bool _isEnabled = true;

    protected SettingRow(string label, string? description = null)
    {
        Label = label;
        Description = description;
    }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool IsDirty { get; protected set; }

    /// <summary>Text of an optional trailing keyword list used by the search box.</summary>
    public string? Keywords { get; init; }

    public bool Matches(string query)
        => Label.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || (Keywords?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    public virtual void Load()
    {
    }

    public virtual Task SaveAsync() => Task.CompletedTask;
}

/// <summary>A bold caption separating groups of rows.</summary>
public sealed class HeaderRow(string label) : SettingRow(label);

/// <summary>An inline explanation, for example "Not available yet".</summary>
public sealed class NoticeRow(string text, bool warning = false) : SettingRow(text)
{
    public bool IsWarning { get; } = warning;
}

/// <summary>A button that runs an action; <see cref="Result"/> shows its outcome.</summary>
public sealed partial class ActionRow : SettingRow
{
    private readonly Func<Task<(bool Ok, string Message)>> _run;

    [ObservableProperty]
    private string? _result;

    [ObservableProperty]
    private bool _resultOk;

    public ActionRow(string label, string buttonText, Func<Task<(bool Ok, string Message)>> run)
        : base(label)
    {
        ButtonText = buttonText;
        _run = run;
    }

    public string ButtonText { get; }

    public async Task RunAsync()
    {
        (ResultOk, Result) = await _run();
    }
}

public abstract partial class ValueRow<T> : SettingRow
{
    private readonly Func<T> _get;
    private readonly Action<T> _set;
    private bool _loading;

    [ObservableProperty]
    private T _value = default!;

    protected ValueRow(string label, Func<T> get, Action<T> set, bool immediate, string? description)
        : base(label, description)
    {
        _get = get;
        _set = set;
        Immediate = immediate;
        Load();
    }

    public bool Immediate { get; }

    public override void Load()
    {
        _loading = true;
        try
        {
            Value = _get();
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
    }

    public override Task SaveAsync()
    {
        if (IsDirty)
        {
            _set(Value);
            IsDirty = false;
        }

        return Task.CompletedTask;
    }

    partial void OnValueChanged(T value)
    {
        if (_loading)
        {
            return;
        }

        IsDirty = true;
        if (Immediate)
        {
            _set(value);
            IsDirty = false;
        }

        Changed?.Invoke(this);
    }

    /// <summary>Raised after an edit (not after <see cref="Load"/>).</summary>
    public event Action<SettingRow>? Changed;
}

public sealed class BoolRow(string label, Func<bool> get, Action<bool> set, bool immediate = true, string? description = null)
    : ValueRow<bool>(label, get, set, immediate, description);

public sealed partial class TextRow : ValueRow<string>
{
    public TextRow(string label, Func<string> get, Action<string> set, bool immediate = true, string? description = null)
        : base(label, get, set, immediate, description)
    {
    }

    public string? Watermark { get; init; }

    /// <summary>Values offered in a drop-down; the text stays free to edit.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    public bool HasOptions => Options is { Count: > 0 };

    /// <summary>Set to offer a Suggest button; returns the value to fill in, or <see langword="null"/> when there is none.</summary>
    public Func<string?>? Suggest { get; init; }

    public bool CanSuggest => Suggest is not null;

    /// <summary>Raised after <see cref="ApplySuggestion"/> filled in a value.</summary>
    public event Action<TextRow>? Suggested;

    [RelayCommand]
    public void ApplySuggestion()
    {
        if (Suggest?.Invoke() is { Length: > 0 } value)
        {
            Value = value;
            Suggested?.Invoke(this);
        }
    }

    /// <summary>Set to offer a Browse button.</summary>
    public BrowseKind Browse { get; init; }

    public bool CanBrowse => Browse != BrowseKind.None;

    public bool IsPassword { get; init; }
}

public enum BrowseKind
{
    None,
    File,
    Folder,
}

public sealed class IntRow : ValueRow<decimal>
{
    public IntRow(string label, Func<int> get, Action<int> set, int minimum, int maximum, bool immediate = true, string? description = null)
        : base(label, () => get(), v => set((int)v), immediate, description)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    public int IntValue => (int)Value;
}

public sealed class ChoiceRow : ValueRow<string>
{
    public ChoiceRow(string label, IReadOnlyList<string> options, Func<string> get, Action<string> set, bool immediate = true, string? description = null)
        : base(label, get, set, immediate, description)
        => Options = options;

    public IReadOnlyList<string> Options { get; }

    /// <summary>A combo box bound to an enum; names are shown with spaces ("FetchPruneAll" becomes "Fetch prune all").</summary>
    public static ChoiceRow ForEnum<T>(string label, Func<T> get, Action<T> set, IEnumerable<T>? values = null, bool immediate = true, string? description = null)
        where T : struct, Enum
    {
        T[] all = (values ?? Enum.GetValues<T>().Distinct()).ToArray();
        string[] names = all.Select(v => Humanize(v.ToString())).ToArray();
        return new ChoiceRow(
            label,
            names,
            () => Humanize(get().ToString()),
            text => set(all[Math.Max(0, Array.IndexOf(names, text))]),
            immediate,
            description);
    }

    /// <summary>A tri-state value (unset / yes / no) like the original's "Ask / Always / Never" boxes.</summary>
    public static ChoiceRow ForNullableBool(string label, string unset, string yes, string no, Func<bool?> get, Action<bool?> set, bool immediate = true, string? description = null)
        => new(
            label,
            [unset, yes, no],
            () => get() switch { true => yes, false => no, _ => unset },
            text => set(text == yes ? true : text == no ? false : null),
            immediate,
            description);

    private static string Humanize(string name)
    {
        string spaced = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return spaced.Length < 2 ? spaced : spaced[..1] + spaced[1..].ToLower(CultureInfo.InvariantCulture);
    }
}

/// <summary>A titled group of rows, used when a page is composed from several parts.</summary>
public sealed class RowList : ObservableCollection<SettingRow>
{
    public RowList Add(params SettingRow[] rows)
    {
        foreach (SettingRow row in rows)
        {
            base.Add(row);
        }

        return this;
    }
}

/// <summary>A color edited with a color picker; "Use default" goes back to the theme's color.</summary>
public sealed partial class ColorRow : ValueRow<Avalonia.Media.Color>
{
    private readonly Action _useDefault;

    public ColorRow(string label, Func<Avalonia.Media.Color> get, Action<Avalonia.Media.Color> set, Action useDefault, string? description = null)
        : base(label, get, set, immediate: true, description)
        => _useDefault = useDefault;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void UseDefault()
    {
        _useDefault();
        Load();
    }
}
