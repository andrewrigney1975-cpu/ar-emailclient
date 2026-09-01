# Dispatch

A **multi-account IMAP/SMTP desktop mail client** for Windows, built with WinUI 3 (Windows App SDK,
unpackaged, self-contained, x64). It shares the shell — VS Code-style left activity bar, resizable
rail / list / reading panes, custom title bar, theme-aware styling — with the
[winui3-fileexplorer](../winui3-fileexplorer) project.

It began as an MVP and has grown into a full day-to-day client: conversation threading, HTML
compose, tags, favourites, follow-ups, a built-in calendar, and an **on-device AI** assistant that
never touches the network.

> **Data directory:** `%LocalAppData%\WinUI3Mail\` (accounts, cache, settings, downloaded AI
> models). The folder name predates the "Dispatch" rebrand and is kept so existing installs keep
> working.

---

## Accounts

- **Any IMAP + SMTP account.** Enter host / port / SSL for each server, or let them be auto-guessed
  from the address for Gmail, Outlook / Office 365, Yahoo, iCloud and Fastmail.
- Credentials are **verified against both servers** before the account is saved.
- **Edit account** and **Remove account** from the account's right-click menu. Removing an account
  also clears its cached messages.
- Passwords are stored **DPAPI-encrypted** (current-user scope) in `accounts.json` — never in the
  clear. App passwords are recommended (and required where 2FA is on).
- Multiple accounts run side by side in one tree.

## Folder tree

- Every account's folders in the left rail, **nested to any depth**, with live unread badges.
- Loads instantly from the local cache, then refreshes from the server.
- **Create** a folder at the account root or as a subfolder of any folder.
- **Rename** and **Delete** folders. Delete asks for confirmation, names the subfolder count, and
  **cascades** — subfolders are removed depth-first so the server accepts the delete, and the local
  cache is purged for every descendant.
- **Drag a folder** onto another folder (or onto the account root) to re-parent it on the server.
- **Expand / collapse state is remembered** per folder, across restarts and background syncs.

## Smart Folders

A pseudo-account pinned above the real accounts, aggregating across every account:

| Smart folder | Shows |
| --- | --- |
| **Inbox** | all accounts' inboxes combined |
| **Unread Mail** | every unread message |
| **Sent Items** | all accounts' sent mail |
| **Favourites** | messages you've starred |
| **Follow Up** | messages flagged for follow-up that aren't done |
| **Tags** | one child per hashtag; opens all messages carrying that tag |

## Message list

- The folder's most recent messages: sender / subject / preview / date, unread dot and bolding,
  priority marker, flag / favourite / attachment icons.
- Backed by a **SQLite cache** (`cache.db`) so a folder shows its last-known contents immediately
  while the live IMAP fetch runs. The cache also drives **search**.
- **Date-group headers** ("Today", "Yesterday", "Last Week", …). Collapsed groups stay collapsed
  across restarts *and* across poll-based syncs.
- **Conversation threading** — messages with the same subject collapse into an expandable thread;
  expand state also survives sync.
- **Multi-select** — `Ctrl`-click to toggle a row, `Shift`-click to select a range, `Ctrl`+`A` to
  select all, `Esc` to clear. Bulk actions (mark read / unread, delete, move, tag) and drag-to-move
  all act on the whole selection.
- **Drag messages** onto a folder in the rail to move them.
- The message open in the reading pane and every selected row get a **left accent bar**.
- **Mark all as read** at the top of the list, with a confirmation toast.

## Reading pane

- **HTML bodies** render in a WebView2 with **JavaScript disabled** and **remote images blocked**
  by default. "Load remote images" reveals them for that message; "Always load remote images for
  this sender's domain" adds the domain to an allow-list (`remote-image-domains.json`).
- **Plain-text bodies** render in a selectable text view.
- **Attachments bar** — preview an attachment in-app (QuickLook-style, like the file-explorer
  project) or download it. Images, PDF, HTML/SVG and text render directly; **Word, Excel and
  PowerPoint** files are converted to PDF on the fly (Syncfusion) and shown in the WebView2.
- Opening a message marks it read on the server.
- Toolbar: Reply, Reply-all, Forward, **Delete** (also on the message right-click menu). Delete
  moves the message to the server's Trash (or flags + expunges if already there).

## Compose

- **Rich WYSIWYG HTML editor** hosted in the reading pane — formatting, inline media, and file
  attachments.
- **New / Reply / Reply-all / Forward**, with correct `In-Reply-To` / `References` threading and
  the quoted original.
- **Recipient auto-complete** from addresses harvested into the contact store.
- **Signatures** — inserted automatically into new mail and replies.

## Contacts

- **Contacts button** in the activity bar switches to a three-pane address book: a **groups
  rail**, a **contact editor**, and a **contact list**.
- A contact has a name / nickname / company, **typed emails** (personal / work / …), **typed
  phone numbers** (personal & work × mobile & landline, fax, …), **typed postal addresses**, a
  **date of birth**, **group** membership, **tags**, and a **photo**.
- **Groups** are user-created and extensible — add / rename / delete from the rail, and
  **favourite** a group or an individual contact (favourites sort to the top).
- List view / **tiles** view / **photos** view, sorted by name or by group, with a search box.
- **Import** from CSV (Outlook / Google export headers), JSON (this app's export or generic
  objects), or HTML (`mailto:` links and bare addresses) — de-duplicated on email.
- Contacts feed compose **recipient auto-complete** alongside addresses harvested from mail.
- Stored locally in `address-book.json`.

## Tags, favourites, follow-ups, priority

- **Hashtags** — add / remove tags on any message from its context menu; each tag becomes a Smart
  Folder child.
- **Favourites** — star a message; see them all under the Favourites smart folder.
- **Follow-up flag with a due date** — flagging a message adds a **calendar event** for the due
  date. "Mark follow-up complete" clears it.
- **Priority** — `Importance` / `Priority` / `X-Priority` headers surface as a badge in the list
  and reading pane.

## Calendar

- **Right-rail mini calendar**, toggled from a button in the title bar next to the window controls.
  Its visibility and the rail width are persisted.
- **Upcoming events**, grouped by month, listed under the calendar.
- **Full calendar mode** — a dedicated view with **Month / Week / Working Week / 3-Day / Day**
  layouts, drawn as a custom grid, with an inline event editor.
- First day of the week **follows the OS** regional setting.
- Events are stored locally in `calendar-events.json`.

## Detecting dates & actions in mail

- The **date/action scanner** reads an incoming message's body and, when it looks like it contains
  a due date or a required action (e.g. a utility bill), offers an **"Add to calendar"** button
  pre-filled with the detected date and a title derived from the sender / subject.

## Notifications

- **Windows toast** on new mail and when a calendar event is **one day away**.
- Clicking a new-mail toast **opens the app and the message**.
- Toasts are **single-instance** (they don't stack up).
- **Server push (IMAP IDLE)** — a dedicated connection per account watches the INBOX and reacts to
  new mail within seconds. A background **poll is the fallback**: every 2 minutes normally,
  dropping to every 15 minutes once every account has a live IDLE connection, and back to 2
  minutes if push drops or a server lacks the IDLE capability.
- The last-opened folder is loaded first on launch.

## On-device AI (optional, no network)

Off until you opt in. On first use the model (**Phi-3.5-mini INT4**, ~2 GB) downloads once into
`%LocalAppData%\WinUI3Mail\models\`. Inference runs locally via **ONNX Runtime GenAI** on
**DirectML** (any DX12 GPU) with an automatic **CPU fallback**. No email content ever leaves the
machine.

| Feature | What it does |
| --- | --- |
| **Summarise** | a short summary + extracted action for the open message |
| **Suggest replies** | a few one-tap reply drafts based on the thread |
| **Compose from prompt** | write / rewrite a message body from a plain-language instruction |
| **Briefings** | a daily and a weekly digest of your mail, cached until stale |

Configure and download models from the **AI settings** dialog (brain icon in the activity bar).

## Persistence & data files

Everything lives under `%LocalAppData%\WinUI3Mail\`:

| File | Contents |
| --- | --- |
| `accounts.json` | accounts; passwords DPAPI-encrypted |
| `settings.json` | pane widths, last folder, calendar state, collapsed groups, expanded folders, AI opt-in, cached briefings |
| `cache.db` | SQLite: folder list, message summaries, tags, favourites, follows, AI summaries/replies |
| `calendar-events.json` | calendar events |
| `contacts.json` | addresses harvested from mail for auto-complete |
| `address-book.json` | the Contacts address book and groups |
| `remote-image-domains.json` | sender domains allowed to load remote images |
| `models/` | downloaded ONNX AI models |
| `syncfusion.license` | Syncfusion Community key for Office previews (optional) |
| `app.log` / `crash.log` | diagnostics (next to the exe) |

---

## Build

The Windows App SDK packaging targets mean `dotnet build` fails — build with **Visual Studio 18's
MSBuild**:

```powershell
& "F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    "F:\Src\winui3-mailclient\src\MailClient\MailClient.csproj" `
    /p:Configuration=Debug /p:Platform=x64 /v:minimal /m
```

Output: `src\MailClient\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Dispatch.exe`

Every build auto-increments `src/MailClient/BuildNumber.txt` (gitignored) and stamps the number
into the assembly; it shows in the title bar (`Dispatch — build N`) and is available at
`MailClient.BuildInfo.Number`.

## Dependencies

| Package | Why |
| --- | --- |
| `Microsoft.WindowsAppSDK` | WinUI 3 |
| `Microsoft.Web.WebView2` | HTML message rendering + rich compose editor |
| `MailKit` / `MimeKit` | IMAP, SMTP, MIME parsing |
| `Microsoft.Data.Sqlite` | message-list cache & search |
| `CommunityToolkit.Mvvm` | `ObservableObject` / source-generated properties |
| `System.Security.Cryptography.ProtectedData` | DPAPI password encryption |
| `Microsoft.ML.OnnxRuntimeGenAI.DirectML` | on-device LLM inference |
| `Syncfusion.DocIO/XlsIO/Presentation` (+ renderers) | Office attachment → PDF preview |

### Syncfusion licence

The Office-preview packages need a **Syncfusion Community licence key** (free for individuals and
small teams). Put the key in a `SYNCFUSION_LICENSE_KEY` environment variable, or in a plain-text
file `%LocalAppData%\WinUI3Mail\syncfusion.license`. Without a key the previews still render but
carry an evaluation banner.

## Project layout

```
src/MailClient/
  App.xaml(.cs), Program.cs               single-instance startup
  MainWindow.xaml(.cs)                    shell + orchestration
  MainWindow.Folders.cs                   folder create/rename/delete, expansion persistence
  MainWindow.DragDrop.cs                  message + folder drag-and-drop
  MainWindow.Selection.cs                 multi-select, current-message highlight
  MainWindow.Calendar.cs                  full calendar mode (month/week/work-week/3-day/day)
  Models/       MailAccount, MailNode, MailListNode, MessageRow, MailMessageContent,
                CalendarEvent, MessageSummary, OutgoingAttachment, ComposeMode
  Services/     AccountStore, SecretProtector, MailService (MailKit), MessageCache (SQLite),
                JsonFileStore, AppSettings, AppPaths, RemoteContentStore, ContactStore,
                CalendarStore, DateActionScanner, NotificationService, LoggingService, BuildInfo
  Services/Ai/  Ai, IAiService, OnnxGenAiService, NullAiService, AiBootstrapper, AiModelManager,
                AiPrompts, AiActionParser, AiReplyParser, AiComposeParser, BriefingBuilder
  ViewModels/   MainViewModel (date-grouped / threaded list, all message operations)
  Views/        AddAccountDialog, AiSettingsDialog, AiBriefingDialog
  Converters/   value converters for the templates
```

## Current limitations

- **No OAuth2** — Gmail / Outlook / Yahoo need an app password.
- IMAP IDLE watches the **INBOX only**; other folders rely on the poll.
- HTML remote-content blocking is **regex-based neutralisation**, not a full DOM/CSS sanitiser.
- AI graph-capture is disabled on DirectML for model compatibility, so GPU inference is not as fast
  as it could be.
