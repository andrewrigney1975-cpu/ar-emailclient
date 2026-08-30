# Backlog

Not yet scheduled. Rough notes on scope so each can be picked up cold.

## Fully-local AI processing
Investigated — see `docs/ai-investigation.md`. Recommendation: an `IAiService`
abstraction backed by ONNX Runtime GenAI + DirectML + Phi-3.5-mini INT4
(downloaded on first opt-in), with an opportunistic Phi Silica backend on
Copilot+ PCs. Phase 1 = reading-pane "Summarise". No feature uses the network.
