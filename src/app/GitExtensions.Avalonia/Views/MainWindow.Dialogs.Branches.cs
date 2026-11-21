using GitExtensions.Desktop.ViewModels;

namespace GitExtensions.Desktop.Views;

// The Merge branches, Rebase and Checkout branch dialogs.
public sealed partial class MainWindow
{
    private void BindBranchDialogs(MainWindowViewModel viewModel)
    {
        DialogHost.Bind(this, viewModel.MergeDialog, () => new MergeDialogView { DataContext = viewModel.MergeDialog }, "Merge branches", 640, 560);
        DialogHost.Bind(this, viewModel.RebaseDialog, () => new RebaseDialogView { DataContext = viewModel.RebaseDialog }, "Rebase", 640, 560);
        DialogHost.Bind(this, viewModel.CheckoutDialog, () => new CheckoutBranchDialogView { DataContext = viewModel.CheckoutDialog }, "Checkout branch", 640, 580);
        DialogHost.Bind(this, viewModel.GitHubAccounts, () => new GitHubAccountsView { DataContext = viewModel.GitHubAccounts }, "GitHub account", 520, 520);
    }
}
