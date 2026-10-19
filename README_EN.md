# OfflineTranslator

[中文](README.md) | **English**

**High-performance, out-of-the-box desktop offline translation assistant and local API service**, tailored for engineering drawings, technical documents, and personal translation workflows.

- License: **MIT**
- Dual Inference Engines: **llama.cpp** (with **Vulkan GPU** hardware acceleration, automatic graceful fallback to CPU) + **HyMT2** (pure managed lightweight engine)
- Instant Millisecond Response: **Thinking / Reasoning disabled by default**, preventing model internal chain-of-thought overhead and delivering direct translations immediately
- Multi-Model Hot-Switching: Ships with **Qwen3 0.6B** (ultra-fast small powerhouse) and **Hy-MT2 1.8B** (Tencent specialized translation model); supports arbitrary generic GGUF models
- Industrial Terminology: Fully compatible with the [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) shared glossary specification (CSV)
- Cross-Session Translation Memory: Persistent local translation memory returning cached segments in **0–10 ms** even across app restarts
- OpenAI-Compatible API: Built-in local HTTP service (default port `21894`) exposing `/v1/chat/completions` with SSE token streaming
- Zero External Dependencies: Self-contained single-file executable; no Python, .NET runtime, or CUDA Toolkit required

---

## 1. Features

| Feature | Description |
|---|---|
| **GPU Hardware Acceleration** | Native llama.cpp Vulkan backend auto-offloads model layers to discrete/integrated GPUs, with seamless fallback to CPU |
| **No Thinking Overhead** | Dual-layer suppression (prompt constraint + streaming state-machine filter) completely blocks `<think>...</think>` output |
| **Dual Engine Architecture** | Proprietary 2-bit formats route to managed HyMT2; standard GGUF models route to native llama.cpp Vulkan/CPU engine |
| **True Streaming Output** | Tokens are rendered in real time, with first token visible within 20–50 ms |
| **Shared Engineering Glossary** | Reads `%ProgramData%\WISDRI\Glossary\glossary.csv` and user overrides with bidirectional matching |
| **Persistent Translation Memory** | Automatically stores translated segments under `%LocalAppData%\WISDRI\OfflineTranslator\` |
| **OpenAI-Compatible API** | Standard `/v1/chat/completions` endpoint for CAD plugins and third-party tools |

---

## 2. Benchmark (Quadro P2200 / Xeon W-2135)

| Model | Engine | Compute Backend | First Token Latency | 10 Lines Total | Recommended Scenario |
|---|---|---|---|---|---|
| **Qwen3 0.6B Q4_K_M** | **llama.cpp** | **Vulkan (GPU)** | **20–40 ms** | **1.2–2.5 s** | **Ultra-fast translation on laptops / PCs with GPU** |
| **Hy-MT2 1.8B 2Bit** | **HyMT2Sharp** | **CPU (P-cores)** | **110–150 ms** | **~2.6 s** | **High-precision mechanical and drawing terminology** |
| **Cached Segments** | **Translation Memory** | **Memory / Disk** | **0 ms** | **0–10 ms** | **Repetitive drawing text and UI strings** |

---

## 3. Quick Start

### 3.1 Download and Run
1. Download the release package `OfflineTranslator-v1.0.0-win-x64.zip` from [GitHub Releases](https://github.com/zly258/OfflineTranslator/releases).
2. Extract to a local directory (e.g., `D:\OfflineTranslator`).
3. Ensure the folder layout matches below and launch `OfflineTranslator.exe`:

```text
OfflineTranslator\
├── OfflineTranslator.exe       # Self-contained executable
├── models\                     # Model weights
│   ├── Qwen\
│   │   └── Qwen3-0.6B-Q4_K_M.gguf
│   └── Hy-MT2\
│       └── Hy-MT2-1.8B-2Bit.gguf
├── runtimes\                   # Windows native libraries
│   └── win-x64\
└── 使用说明.txt
```

### 3.2 UI Operations
- **Model Switching**: Select any discovered model in the dropdown list and click "Switch to this Model" for instant hot-reloading.
- **Backend Status**: The status bar shows the active model, engine, and compute device (e.g., `llama.cpp [Vulkan GPU]` or `HyMT2 [CPU]`).
- **Shortcut**: Press `Ctrl + Enter` in the input box to start translation immediately.

---

## 4. Glossary & Translation Memory

### 4.1 Engineering Glossary
Reads CSV glossary files following the [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) convention:
- **Canonical**: `C:\ProgramData\WISDRI\Glossary\glossary.csv` (overrideable via `WISDRI_GLOSSARY_DIR`)
- **Per-User**: `C:\Users\<User>\AppData\Roaming\WISDRI\Glossary\glossary.csv` (takes priority)

### 4.2 Translation Memory
- Persisted at `%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl`.
- Isolated by model file hash and glossary content hash.

---

## 5. Local OpenAI-Compatible API

Start the API service from the UI (default port `http://127.0.0.1:21894`).

### `POST /v1/chat/completions`

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

---

## 6. Build from Source

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Vulkan 1.2+ capable GPU driver

### Build Command
```powershell
# 1. Clone repository (model files are excluded from git)
git clone git@github.com:zly258/OfflineTranslator.git
cd OfflineTranslator

# 2. Place models into models/ (e.g., models/Qwen/Qwen3-0.6B-Q4_K_M.gguf)

# 3. Build single-file executable and run self-check
.\build.ps1
```

---

## 7. License

Licensed under the [MIT License](LICENSE).
Underlying model weights and inference engines are subject to their respective original licenses.
