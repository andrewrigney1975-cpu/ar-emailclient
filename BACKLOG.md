# Backlog

Not yet scheduled. Rough notes on scope so each can be picked up cold.

## Fully-local AI processing
Investigate the best way to run on-device (no cloud) LLM inference in the app,
then use it for:
- message summaries in the reading pane
- suggested replies / "compose from prompt"
- a weekly digest summary
- a "Today" view summarising the day's events + flagged mail
- smarter email parsing to extend the DateActionScanner (event/action
  identification, calendar actions)
Options to weigh: ONNX Runtime GenAI + a small quantised model (Phi-3-mini,
Llama-3.2-1B/3B), llama.cpp via a native dep, or Windows AI APIs / Foundry
Local. Consider model download/size, CPU vs NPU/GPU, and a graceful
"AI features off" path.
