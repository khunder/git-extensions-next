using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace GitExtensions.Desktop.Views;

/// <summary>
///  Small code-built modal dialogs for the actions of context menus that have no view-model of their own: a confirmation
///  (optionally with a text input) and a notice. They are <see cref="DialogWindow"/>s like every other dialog.
/// </summary>
public static class QuickDialog
{
    /// <summary>
    ///  Asks a question. <paramref name="inputText"/> (when not null) adds a text box with that initial text; the callback
    ///  gets the entered text on OK and is not called on Cancel.
    /// </summary>
    public static DialogWindow Ask(Window owner, string title, string message, string confirmText, Action<string> confirmed, string? inputText = null, bool destructive = false, string? watermark = null)
    {
        TextBox? input = inputText is null ? null : new TextBox { Text = inputText, PlaceholderText = watermark, Name = "QuickDialogInput" };
        DialogWindow? dialog = null;
        Button ok = new() { Content = confirmText, MinWidth = 100, IsDefault = true, Name = "QuickDialogConfirm" };
        if (!destructive)
        {
            ok.Classes.Add("accent");
        }

        Button cancel = new() { Content = "Cancel", MinWidth = 90, IsCancel = true, Name = "QuickDialogCancel" };
        ok.Click += (_, _) =>
        {
            string text = input?.Text ?? string.Empty;
            dialog?.Close();
            confirmed(text);
        };
        cancel.Click += (_, _) => dialog?.Close();
        dialog = Create(owner, title, message, input, [ok, cancel], height: input is null ? 170 : 210);
        return dialog;
    }

    /// <summary>Asks a yes/no question and completes with the answer (<see langword="false"/> when closed or cancelled).</summary>
    public static Task<bool> AskAsync(Window owner, string title, string message, string confirmText, bool destructive = false)
    {
        TaskCompletionSource<bool> answer = new();
        DialogWindow? dialog = null;
        Button ok = new() { Content = confirmText, MinWidth = 100, IsDefault = true, Name = "QuickDialogConfirm" };
        if (!destructive)
        {
            ok.Classes.Add("accent");
        }

        Button cancel = new() { Content = "Cancel", MinWidth = 90, IsCancel = true, Name = "QuickDialogCancel" };
        ok.Click += (_, _) =>
        {
            answer.TrySetResult(true);
            dialog?.Close();
        };
        cancel.Click += (_, _) => dialog?.Close();

        // Longer messages (a warning paragraph) get a taller window instead of being cut off.
        double height = 150 + (Math.Ceiling(message.Length / 70.0) * 20);
        dialog = Create(owner, title, message, null, [ok, cancel], height: Math.Clamp(height, 170, 360));
        dialog.Closed += (_, _) => answer.TrySetResult(false);
        return answer.Task;
    }

    /// <summary>Shows a message with a Close button.</summary>
    public static DialogWindow Notice(Window owner, string title, string message)
    {
        DialogWindow? dialog = null;
        Button close = new() { Content = "Close", MinWidth = 90, IsDefault = true, IsCancel = true, Name = "QuickDialogClose" };
        close.Click += (_, _) => dialog?.Close();
        dialog = Create(owner, title, message, null, [close], height: 170);
        return dialog;
    }

    private static DialogWindow Create(Window owner, string title, string message, Control? input, Button[] buttons, double height)
    {
        StackPanel buttonRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttonRow.Children.AddRange(buttons);
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Name = "QuickDialogMessage" });
        if (input is not null)
        {
            body.Children.Add(input);
        }

        DockPanel panel = new() { LastChildFill = true };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        buttonRow.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(buttonRow);
        panel.Children.Add(body);
        Border root = new() { Padding = new Thickness(12), Child = panel };
        root[!Border.BackgroundProperty] = new DynamicResourceExtension("DialogBackground");
        DialogWindow dialog = new(title, root, 500, height, canResize: false);
        _ = dialog.ShowDialog(owner);
        return dialog;
    }
}
