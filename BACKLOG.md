# Backlog

Not yet scheduled. Rough notes on scope so each can be picked up cold.

## Complete Smart Folders
`Unread Mail` is done. Still to add under the "Smart Folders" quasi-account:
- **Aggregated Inbox** — newest messages across every account's inbox.
- **Sent Items** — aggregated, using each account's `\Sent` special folder.
- **Favourites** — a local store of starred messages (account id + folder + uid),
  a star toggle in the list/reading pane, and the aggregated view.
Needs: per-account special-folder resolution (MailService already resolves Trash
via `SpecialFolder`), a `FavouritesStore`, and cross-account cache queries in
`MessageCache` (see `LoadUnread`).

## Signatures
Per-account signature text (plain, later HTML). Store on `MailAccount`
(`Signature`), edit in the Add/Edit Account dialog, append on New/Reply/Forward
in `StartCompose` (above the quoted block).

## Priority
Read `MimeMessage.Priority` / `Importance` / `X-Priority` in `GetSummariesAsync`
and `GetMessageContent`; persist on the summary row; show a marker in the list
and reading pane. Let the composer set High/Normal/Low.

## Single-instance toast activation
Toast clicks are handled in-process (`NotificationInvoked`) and on cold launch
(`RouteNotificationLaunch`). Add `AppInstance` key registration +
`RedirectActivationToAsync` so a toast click can never spin up a second window
when one is already running.

