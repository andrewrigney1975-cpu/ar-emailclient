# WinUI3 Mail

A minimal **multi-account IMAP/SMTP desktop mail client** for Windows, built with WinUI 3 (Windows
App SDK, unpackaged, self-contained). It reuses the shell — VSCode-style left activity bar,
resizable rail + list + reading panes, custom Mica title bar, theme-aware styling — from the
[winui3-fileexplorer](../winui3-fileexplorer) project.

This is an honest MVP, not a Thunderbird replacement. It does the core loop well and deliberately
leaves the hard 80% (OAuth for Gmail/Microsoft, IDLE push, full HTML sanitisation, calendar/contacts)
for later.

## What works

- **Multiple accounts** — add any IMAP+SMTP account (host/port/SSL for each, or auto-guessed for
  Gmail / Outlook / Yahoo / iCloud / Fastmail from the address). Credentials are verified against
  both servers before the account is saved.
- **Folder tree** — every account's folders in the left rail, with unread badges.
- **Message list** — the folder's most recent ~80 messages; from / subject / preview / date, unread
  dot + bolding, attachment marker. Backed by a small SQLite cache so a folder shows its last-known
  contents instantly while the live IMAP fetch runs.
- **Reading pane** — HTML bodies in a WebView2 (scripts disabled; **remote images blocked** by
  default with a one-click "Load remote images" per message), plain-text bodies in a selectable
  text view. Attachment list. Opening a message marks it read on the server.
- **Compose / Reply / Reply-all / Forward** — plain-text composer in its own window, correct
  `In-Reply-To` / `References` threading, quoted original.
- **Delete** — moves to the server's Trash folder (or flags + expunges if already there).

## Security notes

- Passwords are stored **DPAPI-encrypted** (current-user scope) in
  `%LocalAppData%\WinUI3Mail\accounts.json` — never in the clear. App passwords are recommended.
- The message WebView2 runs with **JavaScript disabled** and **remote content neutralised** by
  regex until you opt in per message. This is lighter than a real HTML sanitiser (no DOM parse,
  no CSS sandbox) — adequate for an MVP, not a hardened client.
- No OAuth yet: Gmail/Outlook/Yahoo require an **app password** (2FA must be on to generate one).

## Not done (deliberate MVP scope)

OAuth2 (Gmail/Microsoft), IMAP IDLE / push notifications, search, conversation threading in the
list, rich-text/HTML compose, drafts, signatures, address book, multiple-identity send, rules /
filters, offline body cache, calendar & contacts.

## Build

Same toolchain constraint as the file explorer: the Windows App SDK packaging targets mean
`dotnet build` fails — build with Visual Studio's MSBuild.

```powershell
& "F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    "F:\Src\winui3-mailclient\src\MailClient\MailClient.csproj" `
    /p:Configuration=Debug /v:minimal /m /nr:false
```

Output: `src\MailClient\bin\Debug\net8.0-windows10.0.19041.0\win-x64\winui3-mailclient.exe`

## Dependencies

| Package | Why |
| --- | --- |
| `Microsoft.WindowsAppSDK` | WinUI 3 |
| `Microsoft.Web.WebView2` | HTML message rendering |
| `MailKit` / `MimeKit` | IMAP, SMTP, MIME parsing |
| `Microsoft.Data.Sqlite` | message-list cache |
| `CommunityToolkit.Mvvm` | `ObservableObject` / source-gen properties |
| `System.Security.Cryptography.ProtectedData` | DPAPI password encryption |

## Project layout

```
src/MailClient/
  App.xaml(.cs), MainWindow.xaml(.cs)   shell + orchestration
  Models/       MailAccount, MailNode, MailListNode, MessageRow, MailMessageContent, ComposeMode
  Services/     AccountStore, SecretProtector, MailService (MailKit), MessageCache (SQLite folders +
                summaries + search), JsonFileStore, AppSettings, RemoteContentStore, LoggingService, AppPaths
  ViewModels/   MainViewModel (date-grouped / threaded message list)
  Views/        AddAccountDialog   (compose is hosted in the reading pane)
  Helpers/      ColumnSplitterController
  Converters/
```
