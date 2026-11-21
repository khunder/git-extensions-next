using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Desktop.Services;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Desktop.ViewModels;

/// <summary>
///  Fetch, pull and push for the current repository, with a live output log.
/// </summary>
public sealed partial class RemoteOperationsViewModel : ObservableObject
{
    // Captured on the creating (UI) thread: callers run on pool threads, and touching the static Dispatcher.UIThread there
    // can create a bogus dispatcher while a headless test session is being set up.
    private readonly Avalonia.Threading.Dispatcher _dispatcher = Avalonia.Threading.Dispatcher.UIThread;

    private readonly Func<GitModule?> _moduleProvider;
    private readonly Action<string> _reportStatus;
    private readonly IMessageDialogs? _messages;
    private readonly Func<Task> _refreshAll;
    private readonly IAskPassProvider? _askPass;
    private readonly OutputAccumulator _output = new();
    private readonly object _outputLock = new();
    private CancellationTokenSource? _cancellation;
    private int _flushPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    private bool _hasRepository;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private bool _isOutputVisible;

    [ObservableProperty]
    private string? _selectedRemote;

    [ObservableProperty]
    private bool _fetchAll;

    [ObservableProperty]
    private bool _prune;

    [ObservableProperty]
    private string? _currentBranch;

    [ObservableProperty]
    private bool _hasUpstream;

    /// <param name="moduleProvider">Gets the repository to operate on (called on the UI thread), or null if none is open.</param>
    /// <param name="reportStatus">Shows a message in the status bar.</param>
    /// <param name="messages">Receives failures so they show up in the error banner.</param>
    /// <param name="refreshAll">Reloads branch, history, changes and refs after a successful operation.</param>
    /// <param name="askPass">Lets git prompt for credentials in the window; when null, git fails fast instead.</param>
    public RemoteOperationsViewModel(Func<GitModule?> moduleProvider, Action<string>? reportStatus = null, IMessageDialogs? messages = null, Func<Task>? refreshAll = null, IAskPassProvider? askPass = null)
    {
        _askPass = askPass;
        _moduleProvider = moduleProvider;
        _reportStatus = reportStatus ?? (static _ => { });
        _messages = messages;
        _refreshAll = refreshAll ?? (static () => Task.CompletedTask);
    }

    public ObservableCollection<string> Remotes { get; } = [];

    /// <summary>
    ///  Whether a fetch, pull or push opens the output log by itself. The simple mode turns it off: the log stays hidden
    ///  until asked for (View &gt; Show output log); the status bar and the error banner still report the result.
    /// </summary>
    public bool ShowOutputWhenRunning { get; set; } = true;

    /// <summary>Raised after a fetch, pull or push failed, with its title ("Push") and the error (e.g. to offer another account).</summary>
    public event Action<string, string>? OperationFailed;

    public bool IsIdle => !IsRunning;

    public bool HasMultipleRemotes => Remotes.Count > 1;

    /// <summary>
    ///  Re-reads the remotes, the current branch and its upstream (off the UI thread).
    /// </summary>
    public async Task ReloadStateAsync()
    {
        GitModule? module = _moduleProvider();
        if (module is null)
        {
            HasRepository = false;
            return;
        }

        try
        {
            (IReadOnlyList<string> remotes, string branch, bool hasUpstream) = await Task.Run(() =>
            {
                IReadOnlyList<string> names = module.GetRemoteNames();
                string selected = module.GetSelectedBranch(emptyIfDetached: true);
                bool upstream = selected.Length > 0 && module.GitExecutable.Execute(
                    "rev-parse --abbrev-ref --symbolic-full-name @{upstream}", throwOnErrorExit: false).ExitedSuccessfully;
                return (names, selected, upstream);
            });

            if (!ReferenceEquals(module, _moduleProvider()))
            {
                return;
            }

            Remotes.Clear();
            foreach (string remote in remotes)
            {
                Remotes.Add(remote);
            }

            OnPropertyChanged(nameof(HasMultipleRemotes));
            SelectedRemote = SelectedRemote is not null && remotes.Contains(SelectedRemote)
                ? SelectedRemote
                : remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
            CurrentBranch = branch.Length > 0 ? branch : null;
            HasUpstream = hasUpstream;
            HasRepository = true;
        }
        catch (Exception ex)
        {
            _reportStatus($"Could not read remotes: {ex.Message}");
        }
    }

    private bool CanStart() => HasRepository && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task FetchAsync()
    {
        List<string> args = ["fetch", "--progress"];
        if (Prune)
        {
            args.Add("--prune");
        }

        if (FetchAll || (Remotes.Count > 1 && SelectedRemote is null))
        {
            args.Add("--all");
        }
        else if (SelectedRemote is not null)
        {
            args.Add(SelectedRemote);
        }
        else
        {
            return FailAsync("Fetch", "The repository has no remotes.");
        }

        return RunAsync("Fetch", args);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PullAsync()
    {
        List<string> args = ["pull", "--progress", "--no-edit"];
        if (!HasUpstream)
        {
            if (SelectedRemote is null || CurrentBranch is null)
            {
                return FailAsync("Pull", CurrentBranch is null ? "HEAD is detached; there is no branch to pull." : "The repository has no remotes.");
            }

            args.Add(SelectedRemote);
            args.Add(CurrentBranch);
        }

        return RunAsync("Pull", args);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PushAsync()
    {
        if (CurrentBranch is null)
        {
            return FailAsync("Push", "HEAD is detached; there is no branch to push.");
        }

        List<string> args = ["push", "--progress"];
        if (!HasUpstream)
        {
            if (SelectedRemote is null)
            {
                return FailAsync("Push", "The repository has no remotes.");
            }

            args.Add("--set-upstream");
            args.Add(SelectedRemote);
            args.Add(CurrentBranch);
        }

        return RunAsync("Push", args);
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void ClearOutput()
    {
        lock (_outputLock)
        {
            _output.Clear();
        }

        FlushOutput();
    }

    [RelayCommand]
    private void ToggleOutput() => IsOutputVisible = !IsOutputVisible;

    private Task FailAsync(string title, string message)
    {
        _reportStatus($"{title} failed: {message}");
        _messages?.ShowError(null, message, $"{title} failed");
        return Task.CompletedTask;
    }

    private async Task RunAsync(string title, List<string> args)
    {
        GitModule? module = _moduleProvider();
        if (module is null || IsRunning)
        {
            return;
        }

        using CancellationTokenSource cancellation = new();
        _cancellation = cancellation;
        IsRunning = true;
        IsOutputVisible = IsOutputVisible || ShowOutputWhenRunning;
        _reportStatus($"{title}…");
        int firstLine;
        lock (_outputLock)
        {
            _output.AppendLine("> git " + string.Join(' ', args));
            firstLine = _output.Lines.Count;
        }

        FlushOutput();

        int exitCode = -1;
        string? exception = null;
        bool cancelled = false;
        using AskPassOperation? askPassOperation = _askPass?.Begin(cancellation.Token);
        try
        {
            exitCode = await Task.Run(() => GitProcessRunner.RunAsync(module.GitExecutable, args, OnOutput, cancellation.Token, askPassOperation));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            exception = ex.Message;
        }

        string? failure = null;
        string summary;
        if (cancelled)
        {
            summary = $"{title} cancelled.";
        }
        else if (exception is not null)
        {
            failure = exception;
            summary = $"{title} failed: {exception}";
        }
        else if (exitCode != 0)
        {
            failure = DescribeFailure(firstLine, askPassOperation);
            summary = $"{title} failed: {failure}";
        }
        else
        {
            summary = $"{title} completed.";
        }

        lock (_outputLock)
        {
            _output.AppendLine(summary);
        }

        FlushOutput();
        _cancellation = null;
        LastRunSucceeded = exitCode == 0 && !cancelled && exception is null;
        LastRunSummary = failure ?? summary;

        if (failure is not null)
        {
            _messages?.ShowError(null, failure, $"{title} failed");
            OperationFailed?.Invoke(title, failure);
        }

        // Keep the buttons disabled while the views reload, then report the outcome last (the reload sets its own status).
        if (exitCode == 0 && !cancelled && exception is null)
        {
            await _refreshAll();
        }

        IsRunning = false;
        _reportStatus(summary);
        await ReloadStateAsync();
    }

    private string DescribeFailure(int firstLine, AskPassOperation? askPass)
    {
        string[] lines;
        lock (_outputLock)
        {
            lines = [.. _output.Lines.Skip(firstLine).Where(line => !string.IsNullOrWhiteSpace(line))];
        }

        string text = string.Join(' ', lines.TakeLast(3)).Trim();
        if (text.Length == 0)
        {
            text = "git exited with an error.";
        }

        if (text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            text += askPass is null
                ? " Authentication is required: Git Extensions Next never prompts, so configure a credential helper or an SSH key."
                : askPass.UserCancelled
                    ? " The credential prompt was cancelled."
                    : " Authentication failed: check the credentials you entered.";
        }

        return text;
    }

    // Called from reader threads: buffer the text and coalesce UI updates.
    private void OnOutput(string chunk)
    {
        lock (_outputLock)
        {
            _output.Append(chunk);
        }

        if (Interlocked.Exchange(ref _flushPending, 1) == 0)
        {
            _dispatcher.Post(FlushOutput, DispatcherPriority.Background);
        }
    }

    private void FlushOutput()
    {
        Interlocked.Exchange(ref _flushPending, 0);
        string text;
        lock (_outputLock)
        {
            text = _output.Text;
        }

        OutputText = text;
    }
}
