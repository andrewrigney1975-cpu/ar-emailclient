using MailClient.Models;
using MailClient.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MailClient;

public sealed partial class MainWindow
{
    // Multi-selection state for the message list (Ctrl/Shift/Ctrl+A).
    private readonly List<MailListNode> _selection = new();
    private MailListNode? _selectionAnchor;

    private IEnumerable<MailListNode> FlatMessageNodes()
    {
        IEnumerable<MailListNode> Walk(IEnumerable<MailListNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Kind == MailListKind.Message)
                {
                    yield return n;
                }

                foreach (var c in Walk(n.Children))
                {
                    yield return c;
                }
            }
        }

        return Walk(_vm.ListNodes);
    }

    private void ClearMessageSelection()
    {
        foreach (var n in _selection)
        {
            n.IsSelected = false;
        }

        _selection.Clear();
    }

    private void SetSelected(MailListNode node, bool selected)
    {
        if (selected && !node.IsSelected)
        {
            node.IsSelected = true;
            _selection.Add(node);
        }
        else if (!selected && node.IsSelected)
        {
            node.IsSelected = false;
            _selection.Remove(node);
        }
    }

    private List<MessageRow> SelectedRows() =>
        _selection.Where(n => n.Row is not null).Select(n => n.Row!).ToList();

    private void MessageRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MailListNode { Kind: MailListKind.Message } node)
        {
            return;
        }

        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(VirtualKeyModifiers.Control);
        var shift = mods.HasFlag(VirtualKeyModifiers.Shift);

        if (ctrl)
        {
            SetSelected(node, !node.IsSelected);
            _selectionAnchor = node;
            e.Handled = true;
        }
        else if (shift && _selectionAnchor is { } anchor)
        {
            var flat = FlatMessageNodes().ToList();
            var a = flat.IndexOf(anchor);
            var b = flat.IndexOf(node);
            if (a >= 0 && b >= 0)
            {
                ClearMessageSelection();
                var (lo, hi) = a <= b ? (a, b) : (b, a);
                for (var i = lo; i <= hi; i++)
                {
                    SetSelected(flat[i], true);
                }
            }

            e.Handled = true;
        }
        else
        {
            // Plain click: drop any multi-selection and fall through to the normal open flow.
            ClearMessageSelection();
            _selectionAnchor = node;
        }
    }

    private void MessageTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.A)
        {
            ClearMessageSelection();
            foreach (var n in FlatMessageNodes())
            {
                SetSelected(n, true);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && _selection.Count > 0)
        {
            ClearMessageSelection();
            e.Handled = true;
        }
    }

    /// If the right-tapped row is part of a multi-selection, append bulk actions and return true.
    private bool TryAddBulkMenu(MenuFlyout flyout, MailListNode node)
    {
        if (node.Row is null || !node.IsSelected || _selection.Count < 2)
        {
            return false;
        }

        var rows = SelectedRows();

        var read = new MenuFlyoutItem { Text = $"Mark {rows.Count} as read" };
        read.Click += (_, _) =>
        {
            foreach (var r in rows)
            {
                _vm.SetRead(r, true);
            }
        };

        var unread = new MenuFlyoutItem { Text = $"Mark {rows.Count} as unread" };
        unread.Click += (_, _) =>
        {
            foreach (var r in rows)
            {
                _vm.SetRead(r, false);
            }
        };

        var delete = new MenuFlyoutItem { Text = $"Delete {rows.Count} messages" };
        delete.Click += async (_, _) =>
        {
            foreach (var r in rows)
            {
                await _vm.DeleteAsync(r);
            }

            ClearMessageSelection();
        };

        flyout.Items.Add(read);
        flyout.Items.Add(unread);
        flyout.Items.Add(delete);

        var moveSub = new MenuFlyoutSubItem { Text = $"Move {rows.Count} to" };
        foreach (var (full, name) in AccountFolderTargets(rows[0].AccountId))
        {
            var item = new MenuFlyoutItem { Text = name };
            var dest = full;
            item.Click += async (_, _) =>
            {
                foreach (var r in rows.Where(r => !r.Folder.Equals(dest, StringComparison.OrdinalIgnoreCase)))
                {
                    await _vm.MoveAsync(r, dest);
                }

                ClearMessageSelection();
            };
            moveSub.Items.Add(item);
        }

        if (moveSub.Items.Count > 0)
        {
            flyout.Items.Add(moveSub);
        }

        var addTag = new MenuFlyoutItem { Text = $"Tag {rows.Count} messages…" };
        addTag.Click += async (_, _) =>
        {
            var tag = await PromptTextAsync("Add tag", "Tag", string.Empty, "Add");
            if (!string.IsNullOrWhiteSpace(tag))
            {
                foreach (var r in rows)
                {
                    MessageCache.AddTag(r.AccountId, r.Folder, r.Uid, tag.TrimStart('#'));
                }

                RefreshTagNodes();
            }
        };
        flyout.Items.Add(addTag);

        return true;
    }

    private IEnumerable<(string FullName, string Name)> AccountFolderTargets(string accountId)
    {
        var account = _railNodes.FirstOrDefault(n => n.AccountId == accountId && n.IsAccount);
        if (account is null)
        {
            yield break;
        }

        IEnumerable<(string, string)> Walk(IEnumerable<MailNode> nodes, string prefix)
        {
            foreach (var n in nodes.Where(x => !x.IsSmart && x.FolderFullName.Length > 0))
            {
                yield return (n.FolderFullName, prefix + n.DisplayName);
                foreach (var c in Walk(n.Children, prefix + n.DisplayName + " / "))
                {
                    yield return c;
                }
            }
        }

        foreach (var t in Walk(account.Children, string.Empty))
        {
            yield return t;
        }
    }
}
