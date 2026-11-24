<h1 align="center">OfflineTranslator</h1>

<p align="center">Local Windows desktop translation assistant and OpenAI-compatible HTTP API with dual-engine GGUF inference, shared engineering glossaries, and persistent translation memory.</p>

<p align="center">
  <a href="./README.md">English</a> | <a href="./README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-3776AB?style=flat-square" alt=".NET 10.0"> <img src="https://img.shields.io/badge/Platform-Windows%20x64-4B5563?style=flat-square" alt="Platform: Windows x64"> <img src="https://img.shields.io/badge/Backend-Vulkan%20%2F%20CPU-4B5563?style=flat-square" alt="Backend: Vulkan / CPU"> <img src="https://img.shields.io/badge/API-OpenAI%20Compatible-0891B2?style=flat-square" alt="API: OpenAI Compatible"> <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-22C55E?style=flat-square" alt="License: MIT"></a>
</p>

OfflineTranslator runs local neural translation completely offline on Windows x64 without requiring Python, a system .NET runtime, or CUDA Toolkit. Built for engineering drawings, technical documentation, and local CAD tool integrations, it pairs a WPF desktop interface with a loopback OpenAI-compatible HTTP server (`127.0.0.1:21894`).

## Highlights

| Highlight | Why it matters |
|---|---|
| Dual-engine GGUF routing | `CompositeEngineFactory` routes proprietary 2-bit Hy-MT2 weights (`2bit`, `stq1_0`, `q2_0c`) to the managed `HyMtEngine` (`Sdcb.HyMT2Sharp`) and standard GGUF models (such as Qwen3 or Hy-MT2 Q4/Q8) to `LLamaEngine` (`LLamaSharp` / `llama.cpp`). |
| Vulkan GPU acceleration with CPU fallback | `LLamaEngine` offloads model layers via the `LLamaSharp.Backend.Vulkan.Windows` backend when a compatible GPU is present and falls back to multi-threaded CPU execution otherwise. |
| Reasoning suppression by default | Prompt constraints paired with `ThinkStreamFilter` strip `<think>...</think>` blocks during streaming so models like Qwen3 emit direct translations without chain-of-thought output. |
| Shared engineering glossary | `GlossaryService` reads UTF-8 CSV glossaries compatible with [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) for exact pre-inference lookup (L1) and engine-side terminology sync, falling back to embedded defaults in memory. |
| Cross-session translation memory | `TranslationMemory` persists deterministic segment translations in JSONL (`%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl`), scoped by model identity and glossary content hash. |
| Local OpenAI-compatible API | `OpenAiApiService` exposes `/v1/chat/completions` (with SSE streaming), `/v1/models`, and `/health` over a zero-dependency `TcpListener` bound to `127.0.0.1`. |

## Architecture

```text
┌──────────────────────────────────────────────────────────┐
│      WPF Desktop UI / Local OpenAI HTTP API (:21894)     │
└────────────────────────────┬─────────────────────────────┘
                             │
                             ▼
                ┌────────────────────────┐
                │   TranslationService   │
                └─────┬──────┬──────┬────┘
                      │      │      │
         ┌────────────┘      │      └───────────────┐
         ▼                   ▼                      ▼
┌──────────────────┐ ┌──────────────────┐ ┌─────────────────────────┐
│ GlossaryService  │ │TranslationMemory │ │ CompositeEngineFactory  │
│  (CSV L1 Match)  │ │  (JSONL Cache)   │ │  (Model Format Router)  │
└──────────────────┘ └──────────────────┘ └────────┬───────┬────────┘
                                                   │       │
                                    ┌──────────────┘       └──────────────┐
                                    ▼                                     ▼
                         ┌─────────────────────┐               ┌─────────────────────┐
                         │     LLamaEngine     │               │     HyMtEngine      │
                         │ (llama.cpp Native)  │               │  (Managed C# 2Bit)  │
                         │ - Standard GGUF     │               │ - Hy-MT2 2Bit GGUF  │
                         │ - Vulkan GPU / CPU  │               │ - CPU Execution     │
                         │ - ThinkStreamFilter │               │ - No External DLLs  │
                         └─────────────────────┘               └─────────────────────┘
```

## Quick Install

### Option 1: Portable Release Package

1. Download the Windows x64 archive (for example, `OfflineTranslator-v1.0.0-win-x64.zip`) from [GitHub Releases](https://github.com/zly258/OfflineTranslator/releases).
2. Extract the archive to a local directory (for example, `D:\OfflineTranslator`), keeping the directory layout intact:

```text
OfflineTranslator\
├── OfflineTranslator.exe
├── models\
│   ├── Qwen\
│   │   └── Qwen3-0.6B-Q4_K_M.gguf
│   └── Hy-MT2\
│       └── Hy-MT2-1.8B-2Bit.gguf
├── runtimes\
│   └── win-x64\
└── 使用说明.txt
```

**Requirements:** Windows 10 or 11 (x64). A Vulkan 1.2+ capable GPU driver is recommended for GPU acceleration with `LLamaEngine`; CPU execution works without a dedicated GPU.

### Option 2: Build from Source

**Prerequisites:**

- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- At least one `.gguf` model file placed under `models/` (model weights are excluded from Git)

```powershell
git clone git@github.com:zly258/OfflineTranslator.git
cd OfflineTranslator

# Place one or more .gguf models in models/, for example:
#   models/Qwen/Qwen3-0.6B-Q4_K_M.gguf
#   models/Hy-MT2/Hy-MT2-1.8B-2Bit.gguf

.\build.ps1
```

`build.ps1` publishes a self-contained single-file `dist\OfflineTranslator.exe`, copies `models\` and `assets\使用说明.txt`, and runs `--ui-smoke` and `--autotest`. Pass `-SkipTest` to skip the automated checks.

## Quick Start

1. Launch `OfflineTranslator.exe` from the extracted or `dist\` directory.
2. Wait for the status bar to show the loaded model and active compute backend (such as `llama.cpp [Vulkan GPU]` or `HyMT2 [CPU]`).
3. Choose the source and target languages (or leave source language on auto-detect), enter text in the source box, and press `Ctrl + Enter` to translate.
4. To hot-switch models, pick another `.gguf` model discovered under `models\` from the dropdown and click **Switch to this Model**.
5. Optional: click **Start API** in the UI to expose the local OpenAI-compatible HTTP endpoint on `http://127.0.0.1:21894`.

## Local OpenAI-Compatible API

When started from the desktop UI, `OpenAiApiService` listens on loopback only (`127.0.0.1`, default port `21894`).

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/chat/completions` | Translate text using OpenAI-compatible chat payloads (supports `stream: true` SSE streaming) |
| `GET` | `/v1/models` | Return the currently loaded model identifier |
| `GET` | `/health`, `/healthz` | Return service status and loaded model name |

### Translation Request Example

```bash
curl http://127.0.0.1:21894/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {"role": "system", "content": "Terminology: mating relationship"},
      {"role": "user", "content": "装配体中的零件通过配合关系定位。"}
    ],
    "source_language": "zh",
    "target_language": "en",
    "stream": true
  }'
```

The endpoint also accepts top-level `system_prompt` and `prompt` fields in addition to `messages`, plus optional `temperature`, `top_p`, and `max_tokens` parameters.

## CLI Options

`OfflineTranslator.exe` supports the following headless verification flags:

| Flag | Purpose |
|---|---|
| `--ui-smoke` | Opens and closes `MainWindow` to verify WPF initialization, then exits with code `0` or `1`. |
| `--autotest [--port <port>]` | Runs the end-to-end `SelfCheck` suite with a fake engine and writes `selfcheck-result.txt`. |
| `--sample` | Runs a sample translation flow and writes `sample-result.txt`. |

## Configuration and Integrations

### Shared Engineering Glossary

`GlossaryService` is read-only and follows the [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) UTF-8 CSV format (`Source,Target,SourceLang,TargetLang,CaseSensitive`; lines starting with `#` are comments):

| Priority | Location | Notes |
|---|---|---|
| 1 (Highest) | `%AppData%\WISDRI\Glossary\glossary.csv` | Per-user override |
| 2 | `%ProgramData%\WISDRI\Glossary\glossary.csv` | Shared canonical glossary; override directory via `WISDRI_GLOSSARY_DIR` |
| 3 (Fallback) | Embedded `assets/glossary-defaults.csv` | Loaded in memory when neither external file exists |

### Persistence Paths

| File | Default Path | Environment Override |
|---|---|---|
| Translation memory (JSONL) | `%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl` | `WISDRI_TM_PATH` |
| Last-used model setting | `%AppData%\WISDRI\OfflineTranslator\settings.json` | None |

Translation memory entries are isolated by model identity and glossary content hash, so changing models or updating glossary files automatically invalidates stale entries.

### Reference Measurements

Previously recorded reference timings on Quadro P2200 / Xeon W-2135 hardware:

| Model | Engine | Compute Backend | First Token Latency | 10-Line Total |
|---|---|---|---|---|
| `Qwen3-0.6B-Q4_K_M` | `llama.cpp` | Vulkan (GPU) | 20–40 ms | 1.2–2.5 s |
| `Hy-MT2-1.8B-2Bit` | `HyMT2Sharp` | CPU (P-cores) | 110–150 ms | ~2.6 s |
| Cached segments | Translation Memory | Memory / Disk | 0 ms | 0–10 ms |

## Documentation

| Topic | What it covers | Link |
|---|---|---|
| End-user guide (Chinese) | Installation layout, UI controls, model switching, glossary paths, and local API usage | [使用说明.txt](./assets/使用说明.txt) |
| WPF theme reference | Resource dictionary structure and UI styling conventions | [Theme README](./src/OfflineTranslator.Wpf/Theme/README.md) |

## License

Licensed under the [MIT License](./LICENSE).

Third-party model weights and inference engines (`LLamaSharp` / `llama.cpp`, `Sdcb.HyMT2Sharp`, and any user-supplied GGUF models) remain subject to their respective licenses.
