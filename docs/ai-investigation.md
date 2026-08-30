# Fully-local AI — investigation

Goal: add AI features (summaries, replies, compose-from-prompt, weekly digest,
"Today" brief, smarter email parsing) that run **100% on the user's device** —
no network inference, no API keys, nothing leaves the machine.

_Written against the state of the ecosystem as of early 2026. Versions move fast;
re-check before implementing._

---

## 1. Options

### A. ONNX Runtime GenAI + DirectML  ← recommended default
- NuGet `Microsoft.ML.OnnxRuntimeGenAI.DirectML` (+ CPU fallback in the base package).
- Runs pre-quantised ONNX models: Phi-3.5-mini, Llama-3.2-1B/3B, Qwen2.5-3B,
  Gemma-2-2B, Mistral-7B. INT4 weights, published ready-to-run on Hugging Face
  (`microsoft/Phi-3.5-mini-instruct-onnx`, `onnx-community/*`).
- **DirectML execution provider** → any Direct3D 12 GPU (NVIDIA / AMD / Intel,
  including integrated graphics from ~2018 on). Automatic **CPU fallback**.
- Streaming token generator API. Recent builds support constrained/JSON output
  ("Guidance") — useful for the email-parsing feature.
- Works on **every Windows 10/11 x64 machine** — no Copilot+ requirement.
- Adds ~40–120 MB of native DLLs to the (already self-contained) output.
- No target-framework change needed; `net8.0-windows10.0.19041.0` is fine.
- Licence: the runtime is MIT. Model licences vary (see §3).

### B. Windows AI APIs / Phi Silica  ← opportunistic second backend
- `Microsoft.Windows.AI.Generative.LanguageModel`, `Microsoft.Windows.AI.Text.*`
  (TextSummarizer, TextRewriter) in the Windows App SDK.
- Phi Silica: a ~3.3B SLM **bundled and serviced by Windows** — zero model
  download, runs on the **NPU**, best battery and latency.
- **Requires a Copilot+ PC** (Snapdragon X today; AMD Ryzen AI 300 / Intel
  Lunar Lake added through 2025), Windows 11 24H2+ (build 26100+), and
  **WinAppSDK 1.7+** (we are on 1.6). The `Microsoft.Windows.AI.*` winmds
  already ship with 1.6 but the `LanguageModel` API surface is 1.7+.
- `LanguageModel.GetReadyState()` returns `Ready` /
  `NotSupportedOnCurrentSystem` / `EnsureNeededResources` — trivial to
  runtime-gate so non-Copilot+ machines never touch it.
- Also gives `Microsoft.Windows.AI.ContentSafety` for input/output moderation.

### C. Foundry Local (Microsoft)
- Local inference runtime + model catalogue (`winget install Microsoft.FoundryLocal`),
  OpenAI-compatible endpoint on `localhost`, ONNX Runtime under the hood,
  auto device selection (CPU/GPU/NPU). C# SDK `Microsoft.AI.Foundry.Local`.
- Simplest integration (HTTP) but it is a **separate process/service** the user
  installs or we bundle and lifecycle-manage. Still maturing.
- Good fit later as a power-user "use my Foundry Local instance" option.

### D. LLamaSharp / llama.cpp
- GGUF models, native backends for CPU + CUDA + **Vulkan** (all GPUs) + Metal.
- Largest model ecosystem, GBNF grammars for structured output.
- Bigger native footprint, and LLamaSharp sometimes lags upstream llama.cpp.
- Reasonable fallback if DirectML perf disappoints, but ORT GenAI is the more
  "first-party .NET" path.

### E. Ollama / LM Studio (rejected as primary)
- Excellent DX, OpenAI-compatible, but a **separate install** — not "in the app".
- Keep only as an optional "bring your own local endpoint (OpenAI-compatible URL)"
  setting for people who already run one.

---

## 2. Recommendation

A layered `IAiService` chosen at runtime:

| Priority | Backend | When used | Model |
|---|---|---|---|
| 1 | `WindowsAiService` (Phi Silica) | `LanguageModel.GetReadyState() == Ready` (Copilot+ PC) | Windows-managed, no download |
| 2 | `OnnxGenAiService` (ORT GenAI + DirectML) | everything else, once the user enables AI | **Phi-3.5-mini-instruct ONNX INT4** (default), **Llama-3.2-1B** (low-end option) |
| 3 | `NullAiService` | AI disabled / unavailable | — features hidden |

- Ship with backend **2** working everywhere; add backend **1** later behind a
  WinAppSDK bump — it is pure upside where present and fully guarded elsewhere.
- **Model download on first enable**: ~2.2 GB (Phi-3.5-mini) or ~0.8 GB
  (Llama-3.2-1B) pulled from Hugging Face to
  `%LocalAppData%\WinUI3Mail\models\<id>\`, with a progress dialog, SHA-256
  verification and resume. One-time, clearly explained.
- Inference always off the UI thread; stream tokens into the view.
- Settings pane: **"AI features — everything runs on this device"** toggle,
  a backend readout (NPU / GPU / CPU), model download / remove, disk usage.

Default model rationale: Phi-3.5-mini is the best quality-per-byte in this
size class, has strong instruction-following and JSON output, and is **MIT**
licensed. Llama-3.2-1B is the escape hatch for weak hardware.

---

## 3. Model sizing & licensing (INT4)

| Model | On disk | Rough speed | Notes |
|---|---|---|---|
| Llama-3.2-1B-Instruct | ~0.8 GB | fast on CPU, very fast on iGPU | OK for summaries/extraction, weak replies. Llama Community Licence |
| Phi-3.5-mini-instruct (3.8B) | ~2.2 GB | ~8–15 tok/s CPU · 30–60 iGPU · 60–120 dGPU | best all-rounder, great JSON. **MIT** |
| Llama-3.2-3B-Instruct | ~1.8 GB | similar tier | good replies. Llama Community Licence |
| Qwen2.5-3B-Instruct | ~1.9 GB | similar tier | strong, **Apache-2.0** |

Working-set RAM: ~3–4 GB for a 3.8B INT4 model, ~1.5 GB for 1B.
Llama Community Licence is fine here (well under the 700M-MAU threshold), but if
we want zero licence friction, **Qwen2.5-3B (Apache-2.0)** or **Phi (MIT)** only.

---

## 4. Feature mapping & phased plan

Each phase is roughly half a working session after the plumbing lands.

**Phase 0 — plumbing (~1–2 sessions)**
- `Services/Ai/IAiService.cs`: `IsAvailable`, `Backend`, `CompleteAsync(AiPrompt, ct)`
  (streaming `IAsyncEnumerable<string>`), `CompleteOnceAsync`, `CompleteJsonAsync<T>`.
- `OnnxGenAiService` + `AiModelManager` (download / verify / locate).
- `AiSettings` (on/off, model id, endpoint override) in `AppSettings`.
- Settings UI + first-run download flow.
- Prompt templates in one file; keep them short — small models are prompt-sensitive.
- Guard rails: truncate email body to ~4–6k chars, per-call token/time budget,
  an assertion that no `HttpClient` is reachable from the AI code path.

**Phase 1 — reading-pane "Summarise"** (lowest risk, clear value)
- Button in the reading pane → 2–4 bullet summary + a one-line "action items".
- Cache the summary per `(AccountId, Folder, Uid)` in a new SQLite table so it is
  instant on re-open and can feed Phases 5–6.

**Phase 2 — smarter email parsing**
- Feed the email to the model, ask for constrained JSON:
  `{ isActionable, dueDate, action, amount, confidence }`.
- Merge with / fall back to the existing regex `DateActionScanner`; feeds the
  "Add to calendar" suggestion that already exists.

**Phase 3 — suggested replies**
- Thread context → 3 short reply options shown as chips in the reading pane;
  clicking one drops the text into the HTML composer.

**Phase 4 — compose from prompt**
- A prompt field in the composer: _"reply saying I can do Thursday not Friday"_
  → a draft in the editor. Human always reviews before send.

**Phase 5 — "Today" brief**
- New Smart Folder / dashboard: model summarises today's calendar events +
  flagged / high-priority / follow-up mail into a short brief.

**Phase 6 — weekly digest**
- Map-reduce over the week's cached per-message summaries → one digest,
  surfaced on Monday open and/or as a notification.

---

## 5. Package / project changes

- Add `Microsoft.ML.OnnxRuntimeGenAI.DirectML` (pulls
  `Microsoft.ML.OnnxRuntime.DirectML`). Self-contained output grows by the
  native runtime (~tens of MB); **models are downloaded, never bundled**.
- No target-framework change for the ORT path.
- Later, for Phi Silica: WinAppSDK 1.6 → 1.7/1.8, bump
  `<WindowsSdkPackageVersion>` / target to `10.0.26100.0`, and runtime-gate
  every call through `LanguageModel.GetReadyState()`.

---

## 6. Risks & open questions

- **2.2 GB first-run download** is a real cost. Mitigations: offer the 0.8 GB
  1B model, make it an explicit one-time opt-in, resumable, removable.
- **CPU-only / old machines**: a 3.8B model is seconds-per-summary on CPU.
  DirectML on essentially any iGPU from the last ~6 years fixes this; detect and
  tell the user which backend they got.
- **Quality ceiling**: a 3.8B local model will not match cloud models for
  nuanced replies. Keep everything human-in-the-loop (drafts only), set
  expectations in the UI.
- **Content safety**: local models can produce odd output. Optionally run the
  Windows `ContentSafety` moderator where available; otherwise keep outputs as
  editable drafts, never auto-send.
- **Determinism / caching**: cache summaries and parse results keyed by message
  so repeated opens are free and results are stable.

---

## 7. Bottom line

Start with **ONNX Runtime GenAI + DirectML + Phi-3.5-mini INT4**, downloaded on
first opt-in, behind an `IAiService` abstraction. Ship Phase 1 (Summarise)
first. Add the **Phi Silica** backend later as a free win on Copilot+ PCs. No
feature ever needs the network.
