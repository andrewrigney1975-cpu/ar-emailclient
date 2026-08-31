using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MailClient;

public sealed partial class MainWindow
{
    private List<MessageRow> _draggedRows = new();
    private MailNode? _draggedFolder;

    // ----- drag sources (per-row CanDrag, more reliable than TreeView.CanDragItems) -----

    private void MessageRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is MailListNode { Kind: MailListKind.Message, Row: { } row })
        {
            _draggedFolder = null;
            _draggedRows = new List<MessageRow> { row };
            args.AllowedOperations = DataPackageOperation.Move;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(row.SubjectDisplay);
        }
        else
        {
            args.Cancel = true;
        }
    }

    private void FolderRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is MailNode
            { IsAccount: false, IsSmart: false, FolderFullName.Length: > 0 } node)
        {
            _draggedRows = new List<MessageRow>();
            _draggedFolder = node;
            args.AllowedOperations = DataPackageOperation.Move;
            args.Data.RequestedOperation = DataPackageOperation.Move;
            args.Data.SetText(node.DisplayName);
        }
        else
        {
            args.Cancel = true;
        }
    }

    private void Row_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _draggedRows = new List<MessageRow>();
        _draggedFolder = null;
    }

    // ----- drop onto the folder tree -----

    private MailNode? DropTargetFolder(object? originalSource)
    {
        var node = FindNodeInParents(originalSource as DependencyObject);
        return node is { IsAccount: false, IsSmart: false } && node.FolderFullName.Length > 0 ? node : null;
    }

    private MailNode? DropTargetAny(object? originalSource)
    {
        var node = FindNodeInParents(originalSource as DependencyObject);
        return node is { IsSmart: false } ? node : null;
    }

    private void MailTree_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedFolder is not null)
        {
            var target = DropTargetAny(e.OriginalSource);
            var ok = target is not null && target.AccountId == _draggedFolder.AccountId &&
                     target.FolderFullName != _draggedFolder.FolderFullName &&
                     !(target.FolderFullName + "/").StartsWith(_draggedFolder.FolderFullName + "/", StringComparison.OrdinalIgnoreCase) &&
                     ParentPath(_draggedFolder.FolderFullName) != target.FolderFullName;

            e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
            if (ok)
            {
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = false;
                e.DragUIOverride.Caption = target!.IsAccount ? "Move to top level" : $"Move into {target.DisplayName}";
            }

            return;
        }

        var folder = DropTargetFolder(e.OriginalSource);
        var messageOk = _draggedRows.Count > 0 && folder is not null &&
                        _draggedRows.All(r => r.AccountId == folder.AccountId) &&
                        !_draggedRows.All(r => r.Folder.Equals(folder.FolderFullName, StringComparison.OrdinalIgnoreCase));

        e.AcceptedOperation = messageOk ? DataPackageOperation.Move : DataPackageOperation.None;
        if (messageOk)
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = $"Move to {folder!.DisplayName}";
        }
    }

    private async void MailTree_Drop(object sender, DragEventArgs e)
    {
        if (_draggedFolder is { } dragged)
        {
            _draggedFolder = null;
            var target = DropTargetAny(e.OriginalSource);
            if (target is null || target.AccountId != dragged.AccountId)
            {
                return;
            }

            var account = AccountStore.Find(dragged.AccountId);
            if (account is null)
            {
                return;
            }

            try
            {
                await Task.Run(() => MailService.MoveFolderAsync(
                    account, dragged.FolderFullName, target.IsAccount ? string.Empty : target.FolderFullName,
                    CancellationToken.None));
                await ReloadAccountFoldersAsync(account.Id);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("MainWindow.MailTree_Drop (folder)", ex);
                await ShowErrorAsync("Couldn't move folder", ex.Message);
            }

            return;
        }

        var dropFolder = DropTargetFolder(e.OriginalSource);
        var rows = _draggedRows.ToList();
        _draggedRows = new List<MessageRow>();
        if (dropFolder is null || rows.Count == 0)
        {
            return;
        }

        foreach (var row in rows.Where(r => r.AccountId == dropFolder.AccountId))
        {
            await _vm.MoveAsync(row, dropFolder.FolderFullName);
        }
    }

    private static string ParentPath(string fullName)
    {
        var cut = fullName.LastIndexOfAny(new[] { '/', '.' });
        return cut > 0 ? fullName[..cut] : string.Empty;
    }
}
