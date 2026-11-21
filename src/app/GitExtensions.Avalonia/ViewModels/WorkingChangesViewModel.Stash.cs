using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Desktop.ViewModels;

// Stashing from the Changes tab, like GitHub Desktop: "Stash all changes" puts the work aside, a "Stashed changes" entry
// under the file list shows the latest stash of the current branch, which can be restored (popped) or discarded (dropped).
public sealed partial class WorkingChangesViewModel
{
    /// <summary>The latest stash made on the current branch, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranchStash))]
    [NotifyPropertyChangedFor(nameof(BranchStashText))]
    private StashItemViewModel? _branchStash;

    /// <summary>The main area shows the stashed changes instead of the diff of a file.</summary>
    [ObservableProperty]
    private bool _isShowingStash;

    /// <summary>The stashes of the repository (this tab's own list, with the files and diff of the shown stash).</summary>
    public StashViewModel Stashes { get; }

    public bool HasBranchStash => BranchStash is not null;

    public string BranchStashText => BranchStash is { } stash ? $"{stash.Message} · {stash.DateText}" : string.Empty;

    /// <summary>Stashes every change, untracked files included (git stash push --include-untracked).</summary>
    [RelayCommand]
    public Task StashAllAsync()
        => HasNoChanges
            ? Task.CompletedTask
            : StashAsync(new GitArgumentBuilder("stash") { "push", "--include-untracked" }, "Stashed all changes");

    /// <summary>Stashes only the selected files (git stash push --include-untracked -- paths).</summary>
    [RelayCommand]
    public Task StashSelectedAsync()
    {
        IReadOnlyList<WorkingFileRowViewModel> rows = SelectedRows;
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        GitArgumentBuilder arguments = new("stash") { "push", "--include-untracked", "--" };
        foreach (WorkingFileRowViewModel row in rows)
        {
            arguments.Add(row.Name.QuoteNE());
        }

        return StashAsync(arguments, rows.Count == 1 ? $"Stashed {rows[0].Name}" : $"Stashed {rows.Count} files");
    }

    /// <summary>Shows the stashed changes of the branch in the main area.</summary>
    [RelayCommand]
    public void ShowStash()
    {
        if (BranchStash is not null)
        {
            IsShowingStash = true;
        }
    }

    /// <summary>Applies the stash of the branch and removes it (git stash pop; it is kept when there are conflicts).</summary>
    [RelayCommand]
    public async Task RestoreStashAsync()
    {
        if (BranchStash is not { } stash)
        {
            return;
        }

        if (await Stashes.PopAsync(stash))
        {
            IsShowingStash = false;
        }
    }

    /// <summary>Asks to delete the stash of the branch (git stash drop).</summary>
    [RelayCommand]
    public void DiscardStash()
    {
        if (BranchStash is not { } stash)
        {
            return;
        }

        Confirm(
            "Discard the stashed changes? They cannot be recovered.",
            "Discard stash",
            async () =>
            {
                if (await Stashes.DropAsync(stash, confirmed: true))
                {
                    IsShowingStash = false;
                }
            });
    }

    partial void OnIsShowingStashChanged(bool value)
    {
        if (value)
        {
            // The stash replaces the file diff: no file stays selected.
            if (Rows.Any(r => r.IsSelected))
            {
                SelectOnly(null);
            }

            Stashes.SelectedStash = BranchStash;
        }
    }

    private async Task LoadStashesAsync()
    {
        await Stashes.LoadAsync();
        StashItemViewModel? latest = Stashes.Items.FirstOrDefault(s => string.Equals(s.Branch, Branch, StringComparison.Ordinal));
        bool changed = latest?.Name != BranchStash?.Name || latest?.Message != BranchStash?.Message;
        BranchStash = latest;
        if (latest is null)
        {
            IsShowingStash = false;
        }
        else if (IsShowingStash && changed)
        {
            Stashes.SelectedStash = latest;
        }
    }

    private async Task StashAsync(ArgumentString arguments, string done)
    {
        ErrorText = null;
        try
        {
            ExecutionResult result = await Task.Run(() => _module.GitExecutable.Execute(arguments, throwOnErrorExit: false));
            string output = (result.StandardOutput + "\n" + result.StandardError).Trim();
            if (!result.ExitedSuccessfully)
            {
                ReportError($"git stash failed: {output}");
            }
            else if (output.Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            {
                ReportError("There are no local changes to stash.");
            }
            else
            {
                _main.Status = done;
            }
        }
        catch (Exception ex)
        {
            ReportError($"git stash failed: {ex.Message}");
        }

        await AfterStashChangeAsync();
    }

    // The working tree changed (and the stash list): refresh the main window, this tab and the toolbar's stash count.
    private async Task AfterStashChangeAsync()
    {
        await _main.RefreshChangesAsync();
        await LoadStashesAsync();
        await _main.Stash.LoadAsync();
    }
}
