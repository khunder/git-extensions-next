using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GitExtensions.Desktop.Services;
using GitExtensions.Desktop.ViewModels;
using GitExtensions.Desktop.Views;

namespace GitExtensions.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemePalettes.Register(Resources);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Must run on the UI thread: the joinable task context captures it as the "main thread".
            GitEnvironment git = GitEnvironment.Initialize();

            ThemeService.Current.ApplyCurrent();
            EditorZoom.Install();
            RowColors.Apply();

            MainWindow window = new();
            MainWindowViewModel viewModel = new(git, new WindowFolderPicker(window));
            window.DataContext = viewModel;
            viewModel.WindowService = new WindowService(viewModel, window);
            desktop.MainWindow = window;

            // Coming back to the app checks the status again (catches anything the file watcher missed).
            window.Activated += (_, _) => viewModel.OnWindowActiveChanged(isActive: true);
            window.Deactivated += (_, _) => viewModel.OnWindowActiveChanged(isActive: false);

            // A failure in an event handler or command must not close the application: show it in the error banner.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                System.Diagnostics.Trace.TraceError(e.Exception.ToString());
                viewModel.ReportUnexpectedError(e.Exception);
                e.Handled = true;
            };

            WindowStateStore windowState = new();
            windowState.Apply(window);
            window.UseLayoutStore(new LayoutStore());
            window.Closing += (_, _) => windowState.Capture(window);

            _ = viewModel.Recent.LoadAsync();
            _ = viewModel.Dashboard.LoadAsync();

            // "git-extensions-avalonia <path>" opens that repository straight away.
            if (desktop.Args is [string path, ..])
            {
                _ = viewModel.OpenAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
