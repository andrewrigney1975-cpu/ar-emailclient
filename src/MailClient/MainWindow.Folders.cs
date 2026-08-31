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

    private readonly HashSet<MailNode> _expansionHooked = new();

    private static string ExpansionKey(MailNode node) => node.AccountId + "|" + node.FolderFullName;

    // Restore each folder's expand/collapse state from settings.json and keep it in sync
    // as the user expands/collapses nodes. Works at any nesting depth.
    private void ApplyFolderExpansion(MailNode accountNode)
    {
        var saved = new HashSet<string>(AppSettings.Current.ExpandedFolders, StringComparer.Ordinal);

        void Walk(MailNode n)
        {
            foreach (var child in n.Children)
            {
                if (child.FolderFullName.Length > 0)
                {
                    child.IsExpanded = saved.Contains(ExpansionKey(child));

                    if (_expansionHooked.Add(child))
                    {
                        child.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(MailNode.IsExpanded))
                            {
                                PersistFolderExpansion(child);
                            }
                        };
                    }
                }

                Walk(child);
            }
        }

        Walk(accountNode);
    }

    private void PersistFolderExpansion(MailNode node)
    {
        var key = ExpansionKey(node);
        AppSettings.Update(s =>
        {
            s.ExpandedFolders.Remove(key);
            if (node.IsExpanded)
            {
                s.ExpandedFolders.Add(key);
            }
        });
    }

    private static int CountDescendantFolders(MailNode node) =>
        node.Children.Count + node.Children.Sum(CountDescendantFolders);

    private static IEnumerable<string> DescendantFolderPaths(MailNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child.FolderFullName;
            foreach (var deeper in DescendantFolderPaths(child))
            {
                yield return deeper;
            }
        }
    }

    private async Task PromptDeleteFolderAsync(MailAccount account, MailNode node)
    {
        var subCount = CountDescendantFolders(node);
        var detail = subCount > 0
            ? $"Delete “{node.DisplayName}”, its {subCount} subfolder(s) and every message inside? This cannot be undone."
            : $"Delete “{node.DisplayName}” and every message inside? This cannot be undone.";

        var confirm = await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Delete folder",
            Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
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
            foreach (var full in DescendantFolderPaths(node).Append(node.FolderFullName))
            {
                MessageCache.RemoveFolder(account.Id, full);
            }

            await ReloadAccountFoldersAsync(account.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainWindow.PromptDeleteFolderAsync", ex);
            await ShowErrorAsync("Couldn't delete folder", ex.Message);
        }
    }
}
