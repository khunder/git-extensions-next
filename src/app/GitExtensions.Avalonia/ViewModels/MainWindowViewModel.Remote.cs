using GitExtensions.Desktop.Services;

namespace GitExtensions.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private RemoteOperationsViewModel? _remote;

    /// <summary>
    ///  Fetch/pull/push and the operation log. Follows the open repository: it reloads its remotes whenever the branch is (re)loaded.
    /// </summary>
    public RemoteOperationsViewModel Remote
    {
        get
        {
            if (_remote is null)
            {
                _remote = new RemoteOperationsViewModel(() => _module, message => Status = message, _git.Messages, () => OpenAsync(RepositoryPath), AskPass);
                _remote.ShowOutputWhenRunning = !IsSimpleMode;
                _remote.OperationFailed += OnRemoteOperationFailed;
                _remote.PropertyChanged += (_, _) => NotifySyncChanged();
                _remote.Remotes.CollectionChanged += (_, _) => NotifySyncChanged();
                PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Branch) && Branch is not null)
                    {
                        _ = _remote.ReloadStateAsync();
                    }
                };
            }

            return _remote;
        }
    }
}
