using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MailClient;

public sealed partial class MainWindow
{
    private List<MessageRow> _draggedRows = new();

    private void MessageTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        _draggedRows = args.Items
            .OfType<MailListNode>()
            .Where(n => n.Kind == MailListKind.Message && n.Row is not null)
            .Select(n => n.Row!)
            .ToList();

        if (_draggedRows.Count == 0)
        {
            args.Cancel = true;
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(string.Join(", ", _draggedRows.Select(r => r.SubjectDisplay)));
    }

    private void MessageTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args) =>
        _draggedRows = new List<MessageRow>();

    private MailNode? DropTargetFolder(object? originalSource)
    {
        var node = FindNodeInParents(originalSource as DependencyObject);
        return node is { IsAccount: false, IsSmart: false } && node.FolderFullName.Length > 0 ? node : null;
    }

    private void MailTree_DragOver(object sender, DragEventArgs e)
    {
        var folder = DropTargetFolder(e.OriginalSource);
        var ok = _draggedRows.Count > 0 && folder is not null &&
                 _draggedRows.All(r => r.AccountId == folder.AccountId) &&
                 !_draggedRows.All(r => r.Folder.Equals(folder.FolderFullName, StringComparison.OrdinalIgnoreCase));

        e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
        if (ok)
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = $"Move to {folder!.DisplayName}";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void MailTree_Drop(object sender, DragEventArgs e)
    {
        var folder = DropTargetFolder(e.OriginalSource);
        var rows = _draggedRows.ToList();
        _draggedRows = new List<MessageRow>();

        if (folder is null || rows.Count == 0)
        {
            return;
        }

        var destination = folder.FolderFullName;
        foreach (var row in rows.Where(r => r.AccountId == folder.AccountId))
        {
            await _vm.MoveAsync(row, destination);
        }
    }
}
