namespace GitExtensions.Desktop.ViewModels;

public sealed partial class RemoteOperationsViewModel
{
    /// <summary>
    ///  Starts a section in the output log for a command that is not run by this view-model (e.g. clone) and shows the panel.
    ///  Call on the UI thread; follow with <see cref="AppendExternalOutput"/> from any thread.
    /// </summary>
    public void BeginExternalOutput(string commandLine)
    {
        lock (_outputLock)
        {
            _output.AppendLine("> " + commandLine);
        }

        IsOutputVisible = IsOutputVisible || ShowOutputWhenRunning;
        FlushOutput();
    }

    /// <summary>
    ///  Appends raw process output (progress meters included) to the log. Safe to call from any thread.
    /// </summary>
    public void AppendExternalOutput(string chunk) => OnOutput(chunk);

    /// <summary>
    ///  Appends a finished line (such as the outcome) to the log on the UI thread.
    /// </summary>
    public void EndExternalOutput(string summary)
    {
        lock (_outputLock)
        {
            _output.AppendLine(summary);
        }

        FlushOutput();
    }
}
