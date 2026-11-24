<h1 align="center">OfflineTranslator</h1>

<p align="center">面向 Windows x64 的本地离线桌面翻译助手与 OpenAI 兼容 HTTP 服务，支持双引擎 GGUF 推理、共享工程术语库与持久化翻译记忆。</p>

<p align="center">
  <a href="./README.md">English</a> | <a href="./README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-3776AB?style=flat-square" alt=".NET 10.0"> <img src="https://img.shields.io/badge/Platform-Windows%20x64-4B5563?style=flat-square" alt="Platform: Windows x64"> <img src="https://img.shields.io/badge/Backend-Vulkan%20%2F%20CPU-4B5563?style=flat-square" alt="Backend: Vulkan / CPU"> <img src="https://img.shields.io/badge/API-OpenAI%20Compatible-0891B2?style=flat-square" alt="API: OpenAI Compatible"> <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-22C55E?style=flat-square" alt="License: MIT"></a>
</p>

OfflineTranslator 可在 Windows x64 上完全离线运行本地神经网络翻译，无需安装 Python、系统 .NET 运行时或 CUDA Toolkit。程序面向工程图纸、技术文档与本地 CAD 工具集成场景，同时提供 WPF 桌面界面和仅监听本机回环地址（`127.0.0.1:21894`）的 OpenAI 兼容 HTTP API。

## 核心特性

| 特性 | 价值与实现说明 |
|---|---|
| 双引擎智能路由 | `CompositeEngineFactory` 按模型文件格式自动分发：专有 2-bit Hy-MT2 权重（`2bit`、`stq1_0`、`q2_0c`）交由纯托管 `HyMtEngine`（`Sdcb.HyMT2Sharp`）执行，标准 GGUF 模型（如 Qwen3、Hy-MT2 Q4/Q8）交由 `LLamaEngine`（`LLamaSharp` / `llama.cpp`）执行。 |
| Vulkan GPU 加速与 CPU 回退 | `LLamaEngine` 通过 `LLamaSharp.Backend.Vulkan.Windows` 在支持 Vulkan 的显卡上自动进行层卸载，无可用显卡时平滑回退到多线程 CPU。 |
| 默认关闭推理思考过程 | 通过提示词约束与 `ThinkStreamFilter` 流式状态机双重过滤 `<think>...</think>` 区块，使 Qwen3 等模型直接输出最终译文。 |
| 共享工程术语库 | `GlossaryService` 以只读方式消费兼容 [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) 的 UTF-8 CSV 术语库，支持送翻前 L1 精确直出与推理侧术语同步；外部文件缺失时在内存中回退至内置默认词表。 |
| 跨会话持久化翻译记忆 | `TranslationMemory` 将确定性译文以 JSONL 格式持久化到 `%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl`，并按模型身份与术语库内容哈希隔离。 |
| 本地 OpenAI 兼容 API | `OpenAiApiService` 基于无外部依赖的 `TcpListener` 绑定 `127.0.0.1`，提供 `/v1/chat/completions`（支持 SSE 流式分片）、`/v1/models` 与 `/health` 接口。 |

## 架构设计

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

## 快速安装

### 方式一：下载绿色发布包

1. 从 [GitHub Releases](https://github.com/zly258/OfflineTranslator/releases) 下载 Windows x64 发布包（例如 `OfflineTranslator-v1.0.0-win-x64.zip`）。
2. 解压至本机目录（例如 `D:\OfflineTranslator`），保持以下相对目录结构不变：

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

**运行要求：** Windows 10 或 11 (x64)。使用 `LLamaEngine` 进行 GPU 加速时建议配备支持 Vulkan 1.2+ 的显卡驱动；无独立显卡时可直接使用 CPU 推理。

### 方式二：从源码构建

**环境要求：**

- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 在 `models/` 目录下放入至少一个 `.gguf` 模型文件（仓库不含模型权重文件）

```powershell
git clone git@github.com:zly258/OfflineTranslator.git
cd OfflineTranslator

# 将一个或多个 .gguf 模型放入 models/ 目录，例如：
#   models/Qwen/Qwen3-0.6B-Q4_K_M.gguf
#   models/Hy-MT2/Hy-MT2-1.8B-2Bit.gguf

.\build.ps1
```

`build.ps1` 会将自包含单文件程序发布到 `dist\OfflineTranslator.exe`，同步复制 `models\` 与 `assets\使用说明.txt`，并自动执行 `--ui-smoke` 与 `--autotest` 自检。如需跳过自检，可追加 `-SkipTest` 参数。

## 快速开始

1. 双击运行解压目录或 `dist\` 目录下的 `OfflineTranslator.exe`。
2. 等待状态栏显示当前已加载的模型及计算后端（例如 `llama.cpp [Vulkan GPU]` 或 `HyMT2 [CPU]`）。
3. 选择源语言与目标语言（源语言可保持自动检测），在输入框中输入原文，按 `Ctrl + Enter` 执行翻译。
4. 如需热切换模型，在模型下拉列表中选择 `models\` 下扫描到的其它 `.gguf` 模型，点击「切换到此模型」。
5. 如需供本机其它程序调用，点击界面上的「启动 API」即可在 `http://127.0.0.1:21894` 开启本地 OpenAI 兼容服务。

## 本地 OpenAI 兼容 API

在桌面界面启动 API 后，`OpenAiApiService` 仅监听本机回环地址 `127.0.0.1`（默认端口 `21894`）。

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/v1/chat/completions` | OpenAI 兼容翻译接口，支持 `stream: true` SSE 流式输出 |
| `GET` | `/v1/models` | 返回当前已加载模型的名称列表 |
| `GET` | `/health`, `/healthz` | 健康检查接口，返回服务状态与当前模型名 |

### 请求示例

```bash
curl http://127.0.0.1:21894/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "messages": [
      {"role": "system", "content": "术语要求：配合关系 -> mating relationship"},
      {"role": "user", "content": "装配体中的零件通过配合关系定位。"}
    ],
    "source_language": "zh",
    "target_language": "en",
    "stream": true
  }'
```

除 `messages` 外，请求体也支持顶层 `system_prompt` 和 `prompt` 字段，以及可选的 `temperature`、`top_p`、`max_tokens` 参数。

## 命令行参数

`OfflineTranslator.exe` 支持以下无交互校验参数：

| 参数 | 说明 |
|---|---|
| `--ui-smoke` | 启动并关闭 `MainWindow` 以验证 WPF 窗口初始化，完成后返回退出码 `0` 或 `1`。 |
| `--autotest [--port <port>]` | 使用假引擎运行端到端 `SelfCheck` 自动化自检，并输出 `selfcheck-result.txt`。 |
| `--sample` | 执行样例翻译流程，并将结果写入 `sample-result.txt`。 |

## 配置与集成

### 共享工程术语库

`GlossaryService` 以只读方式读取兼容 [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) 规范的 UTF-8 CSV 术语库（表头为 `Source,Target,SourceLang,TargetLang,CaseSensitive`，`#` 开头行为注释）：

| 优先级 | 路径 | 说明 |
|---|---|---|
| 1（最高） | `%AppData%\WISDRI\Glossary\glossary.csv` | 每用户优先覆盖文件 |
| 2 | `%ProgramData%\WISDRI\Glossary\glossary.csv` | 全局共享规范文件，可通过环境变量 `WISDRI_GLOSSARY_DIR` 重定向目录 |
| 3（回退） | 内嵌 `assets/glossary-defaults.csv` | 以上文件均不存在时在内存中生效，不落盘 |

### 持久化文件路径

| 用途 | 默认路径 | 环境变量覆盖 |
|---|---|---|
| 翻译记忆（JSONL） | `%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl` | `WISDRI_TM_PATH` |
| 上次加载的模型记录 | `%AppData%\WISDRI\OfflineTranslator\settings.json` | 无 |

翻译记忆按模型身份与术语库内容哈希进行作用域隔离，切换模型或修改术语库后旧记录会自动失效。

### 参考性能记录

以下为已有记录的 Quadro P2200 / Xeon W-2135 平台参考实测数据：

| 模型 | 推理引擎 | 计算后端 | 单句首字延迟 | 10 行短文耗时 |
|---|---|---|---|---|
| `Qwen3-0.6B-Q4_K_M` | `llama.cpp` | Vulkan (GPU) | 20–40 ms | 1.2–2.5 s |
| `Hy-MT2-1.8B-2Bit` | `HyMT2Sharp` | CPU (性能核) | 110–150 ms | 约 2.6 s |
| 历史命中句段 | 翻译记忆 | 内存 / 本地磁盘 | 0 ms | 0–10 ms |

## 文档导航

| 主题 | 内容说明 | 链接 |
|---|---|---|
| 终端用户使用说明 | 目录结构、界面操作、多模型切换、术语库路径与本地 API 示例 | [使用说明.txt](./assets/使用说明.txt) |
| WPF 主题规范 | 资源字典结构与界面样式约定 | [Theme README](./src/OfflineTranslator.Wpf/Theme/README.md) |

## 许可证

本项目基于 [MIT License](./LICENSE) 开源。

第三方模型权重与推理引擎（`LLamaSharp` / `llama.cpp`、`Sdcb.HyMT2Sharp` 及用户自行引入的 GGUF 模型）分别遵循其各自原始许可协议。
