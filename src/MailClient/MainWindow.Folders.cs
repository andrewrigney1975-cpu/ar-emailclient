using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailClient;

public sealed partial class MainWindow
{
    private async Task ReloadAccountFoldersAsync(string accountId)
    {
        var node = _railNodes.FirstOrDefault(n => n.AccountId == accountId);
        if (node is not null)
        {
            await LoadFoldersAsync(node);
        }
    }

    private async Task<string?> PromptTextAsync(string title, string header, string prefill, string primary)
    {
        var box = new TextBox { Header = header, Text = prefill, Width = 300 };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
    }

    private async Task PromptNewFolderAsync(MailAccount account, string parentFullName)
    {
        var name = await PromptTextAsync(
            parentFullName.Length == 0 ? "New folder" : "New subfolder",
            parentFullName.Length == 0 ? "Folder name" : $"New folder inside “{parentFullName}”",
            string.Empty, "Create");
        if (name is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => MailService.CreateFolderAsync(account, parentFullName, name, CancellationToken.None));
            await ReloadAccountFoldersAsync(account.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.PromptNewFolderAsync", ex);
            await ShowErrorAsync("Couldn't create folder", ex.Message);
        }
    }

    private async Task PromptRenameFolderAsync(MailAccount account, MailNode node)
    {
        var name = await PromptTextAsync("Rename folder", "New name", node.DisplayName, "Rename");
        if (name is null || name == node.DisplayName)
        {
            return;
        }

        try
        {
            await Task.Run(() => MailService.RenameFolderAsync(account, node.FolderFullName, name, CancellationToken.None));
            await ReloadAccountFoldersAsync(account.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.PromptRenameFolderAsync", ex);
            await ShowErrorAsync("Couldn't rename folder", ex.Message);
        }
    }

    private async Task PromptDeleteFolderAsync(MailAccount account, MailNode node)
    {
        var confirm = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete folder",
            Content = new TextBlock
            {
                Text = $"Delete “{node.DisplayName}” and everything in it? This cannot be undone.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (confirm != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() => MailService.DeleteFolderAsync(account, node.FolderFullName, CancellationToken.None));
            MessageCache.RemoveFolder(account.Id, node.FolderFullName);
            await ReloadAccountFoldersAsync(account.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.PromptDeleteFolderAsync", ex);
            await ShowErrorAsync("Couldn't delete folder", ex.Message);
        }
    }
}
