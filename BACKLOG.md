# Backlog

Not yet scheduled. Rough notes on scope so each can be picked up cold.

## Signatures
Per-account signature text (plain, later HTML). Store on `MailAccount`
(`Signature`), edit in the Add/Edit Account dialog, append on New/Reply/Forward
in `StartCompose` (above the quoted block).

## Priority
Read `MimeMessage.Priority` / `Importance` / `X-Priority` in `GetSummariesAsync`
and `GetMessageContent`; persist on the summary row; show a marker in the list
and reading pane. Let the composer set High/Normal/Low.


