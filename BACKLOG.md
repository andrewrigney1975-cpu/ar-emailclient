# Backlog

Not yet scheduled. Rough notes on scope so each can be picked up cold.

## Detailed calendar view
The right-rail calendar is month-grid + event lists only. Add proper views:
month, week, working week (Mon-Fri), 3-day, and single day — with time-of-day
rows for the week/day views. Probably a dedicated calendar pane/window rather
than the narrow right rail.

## Fully-local AI processing
Investigated — see `docs/ai-investigation.md`. Recommendation: an `IAiService`
abstraction backed by ONNX Runtime GenAI + DirectML + Phi-3.5-mini INT4
(downloaded on first opt-in), with an opportunistic Phi Silica backend on
Copilot+ PCs. Phase 1 = reading-pane "Summarise". No feature uses the network.
