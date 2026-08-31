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
            LoggingService.Info("DragDrop", $"message drag start: {row.SubjectDisplay}");
        }
        else
        {
            LoggingService.Info("DragDrop", $"message drag cancel (ctx={(sender as FrameworkElement)?.DataContext?.GetType().Name})");
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

    // ----- drop onto a folder row -----

    private bool _loggedDragOver;

    private void FolderRow_DragOver(object sender, DragEventArgs e)
    {
        if (!_loggedDragOver)
        {
            _loggedDragOver = true;
            LoggingService.Info("DragDrop", $"folder DragOver: ctx={(sender as FrameworkElement)?.DataContext?.GetType().Name}, " +
                $"rows={_draggedRows.Count}, folder={_draggedFolder?.DisplayName ?? "none"}");
        }

        if ((sender as FrameworkElement)?.DataContext is not MailNode target)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var ok = false;
        string caption = string.Empty;

        if (_draggedFolder is { } dragged)
        {
            ok = target.AccountId == dragged.AccountId &&
                 target.FolderFullName != dragged.FolderFullName &&
                 !(target.FolderFullName + "/").StartsWith(dragged.FolderFullName + "/", StringComparison.OrdinalIgnoreCase) &&
                 ParentPath(dragged.FolderFullName) != (target.IsAccount ? string.Empty : target.FolderFullName);
            caption = target.IsAccount ? "Move to top level" : $"Move into {target.DisplayName}";
        }
        else if (_draggedRows.Count > 0)
        {
            ok = target is { IsAccount: false, IsSmart: false } && target.FolderFullName.Length > 0 &&
                 _draggedRows.All(r => r.AccountId == target.AccountId) &&
                 !_draggedRows.All(r => r.Folder.Equals(target.FolderFullName, StringComparison.OrdinalIgnoreCase));
            caption = $"Move to {target.DisplayName}";
        }

        e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
        if (ok)
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = caption;
        }

        e.Handled = true;
    }

    private async void FolderRow_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MailNode target)
        {
            return;
        }

        e.Handled = true;
        var draggedFolder = _draggedFolder;
        var rows = _draggedRows.ToList();
        _draggedFolder = null;
        _draggedRows = new List<MessageRow>();

        if (draggedFolder is not null && AccountStore.Find(draggedFolder.AccountId) is { } folderAccount &&
            target.AccountId == draggedFolder.AccountId)
        {
            try
            {
                await Task.Run(() => MailService.MoveFolderAsync(
                    folderAccount, draggedFolder.FolderFullName,
                    target.IsAccount ? string.Empty : target.FolderFullName, CancellationToken.None));
                await ReloadAccountFoldersAsync(folderAccount.Id);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("MainWindow.FolderRow_Drop (folder)", ex);
                await ShowErrorAsync("Couldn't move folder", ex.Message);
            }

            return;
        }

        if (rows.Count > 0 && target is { IsAccount: false, IsSmart: false } && target.FolderFullName.Length > 0)
        {
            foreach (var row in rows.Where(r => r.AccountId == target.AccountId))
            {
                await _vm.MoveAsync(row, target.FolderFullName);
            }
        }
    }

    private static string ParentPath(string fullName)
    {
        var cut = fullName.LastIndexOfAny(new[] { '/', '.' });
        return cut > 0 ? fullName[..cut] : string.Empty;
    }
}
