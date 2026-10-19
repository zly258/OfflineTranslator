# OfflineTranslator (离线翻译助手)

**中文** | [English](README_EN.md)

**高性能、开箱即用的桌面离线翻译助手与本地 API 服务**，专为工程图纸、技术文档及个人翻译场景打造。

- 许可证：**MIT**
- 双推理引擎：**llama.cpp**（支持 **Vulkan GPU** 硬件加速，自动 fallback 纯 CPU） + **HyMT2**（纯托管超轻量引擎）
- 毫秒级极速响应：**默认彻底关闭大模型思考过程（No Reasoning/Thinking）**，提示词与解码器双重阻断思维链输出，开箱直出最终译文
- 多模型热切换：随包支持 **Qwen3 0.6B**（极速小钢炮）与 **Hy-MT2 1.8B**（腾讯专业翻译模型），支持任意通用 GGUF 自由追加与热切换
- 工业级术语库：兼容 [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) 三端共享术语规范（CSV），支持精准查库直出与动态提示词注入
- 跨会话翻译记忆：本地持久化记忆库，相同句段重启程序后 **0–10 ms** 瞬间返回
- OpenAI 兼容接口：一键启动本地 HTTP 服务（默认端口 `21894`），提供 `/v1/chat/completions` 与 SSE 真流式分片
- 零外部环境依赖：单文件原生运行，免装 Python、.NET 运行库或庞大的 CUDA Toolkit

---

## 1. 核心特性

| 特性 | 说明 |
|---|---|
| **GPU 硬件加速** | 基于 llama.cpp Vulkan 原生后端，独显/集显全自动层卸载（Offload），无可用显卡时平滑回退多线程 CPU |
| **默认关闭思考** | 针对 Qwen3 等带思维链的推理模型，提示词封口 + 流式状态机双重过滤 `<think>...</think>`，杜绝废话耗时 |
| **双引擎智能分发** | 专有 2Bit 格式走纯 C# 轻量引擎；标准 GGUF（Qwen3、Hy-MT2 Q4/Q8 等）走 llama.cpp 原生加速引擎 |
| **逐词真流式输出** | 边生成边渲染，单句 20–50 ms 即见首字，长文本不再出现“假死”感 |
| **共享工程术语库** | 自动读取 `%ProgramData%\WISDRI\Glossary\glossary.csv` 及用户自定义覆盖文件，支持长词优先与反向查词 |
| **持久化翻译记忆** | 自动记忆已翻句段至 `%LocalAppData%\WISDRI\OfflineTranslator\`，模型或术语库变更时自动安全失效 |
| **OpenAI 兼容 API** | 标准 `/v1/chat/completions`，支持系统提示词注入术语要求，完美对接 CAD 插件与第三方工具 |

---

## 2. 性能实测（Quadro P2200 / Xeon W-2135 平台）

| 模型 | 推理引擎 | 计算硬件 | 单句首字延迟 | 10 行短文耗时 | 推荐使用场景 |
|---|---|---|---|---|---|
| **Qwen3 0.6B Q4_K_M** | **llama.cpp** | **Vulkan (GPU 加速)** | **20–40 ms** | **1.2–2.5 s** | **轻薄本/有显卡机器追求极致翻译速度** |
| **Hy-MT2 1.8B 2Bit** | **HyMT2Sharp** | **CPU (性能核)** | **110–150 ms** | **约 2.6 s** | **机械工业与图纸复杂专业术语高精度翻译** |
| **历史命中句段** | **翻译记忆** | **内存/本地磁盘** | **0 ms** | **0–10 ms** | **重复图纸文字、常见 CAD 界面短语瞬间直出** |

---

## 3. 架构设计

```
                    ┌──────────────────────────────────────────────┐
                    │       WPF 桌面界面 / OpenAI HTTP 服务 API      │
                    └──────────────────────┬───────────────────────┘
                                           │
                                ┌──────────▼───────────┐
                                │  TranslationService  │
                                └────┬───────┬───────┬─┘
                                     │       │       │
              ┌──────────────────────┘       │       └──────────────────────┐
              ▼                              ▼                              ▼
     ┌─────────────────┐           ┌───────────────────┐          ┌───────────────────┐
     │  GlossaryService│           │ TranslationMemory │          │  CompositeEngine  │
     │ (L1精确查库直出) │           │ (跨会话持久化记忆) │          │     Dispatcher    │
     └─────────────────┘           └───────────────────┘          └────┬────────────┬─┘
                                                                       │            │
                                       ┌───────────────────────────────┘            └────────┐
                                       ▼                                                     ▼
                            ┌─────────────────────┐                               ┌─────────────────────┐
                            │     LLamaEngine     │                               │     HyMtEngine      │
                            │  (llama.cpp 原生)   │                               │     (纯 C# 托管)     │
                            ├─────────────────────┤                               ├─────────────────────┤
                            │ • Qwen3 / 通用 GGUF  │                               │ • Hy-MT2 2Bit 专有  │
                            │ • Vulkan GPU 加速   │                               │ • CPU SIMD 优化     │
                            │ • ThinkStreamFilter │                               │ • 零外部 DLL 依赖   │
                            └─────────────────────┘                               └─────────────────────┘
```

---

## 4. 快速开始

### 4.1 下载与运行
1. 从 [GitHub Releases](https://github.com/zly258/OfflineTranslator/releases) 下载最新的绿色发布包 `OfflineTranslator-v1.0.0-win-x64.zip`。
2. 解压到本机目录（例如 `D:\OfflineTranslator`）。
3. 保持如下目录结构，双击 `OfflineTranslator.exe` 即可使用：

```text
OfflineTranslator\
├── OfflineTranslator.exe       # 单文件主程序
├── models\                     # 模型文件夹
│   ├── Qwen\
│   │   └── Qwen3-0.6B-Q4_K_M.gguf
│   └── Hy-MT2\
│       └── Hy-MT2-1.8B-2Bit.gguf
├── runtimes\                   # Windows 本地加速运行库
│   └── win-x64\
└── 使用说明.txt
```

### 4.2 界面功能
- **模型切换**：下拉框实时列出 `models\` 下的所有模型，选中后点「切换到此模型」即可一键热切换。
- **状态监控**：状态栏实时反馈当前模型名称、所用引擎及实际计算硬件（如 `llama.cpp [Vulkan GPU 加速]` 或 `HyMT2 [CPU]`）。
- **快捷键**：在输入框内按 `Ctrl + Enter` 立即执行翻译。

---

## 5. 术语库与翻译记忆

### 5.1 共享术语库（只读消费）
程序完全遵照 [OpenCADTranslate](https://github.com/zly258/OpenCADTranslate) 工业规范读取术语库文件（UTF-8 CSV，`#` 开头为注释）：
- **企业全局规范**：`C:\ProgramData\WISDRI\Glossary\glossary.csv`（可用环境变量 `WISDRI_GLOSSARY_DIR` 自定义）
- **每用户优先覆盖**：`C:\Users\<用户名>\AppData\Roaming\WISDRI\Glossary\glossary.csv`

两处文件缺失时，程序内存中使用内置出厂机械工程默认术语（不落盘、不污染系统）。

### 5.2 翻译记忆（持久化）
- 自动记忆已翻译句段，存储于 `%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl`。
- 采用模型身份与术语库内容哈希双重隔离，更换模型或修改术语后旧记忆自动安全失效。

---

## 6. 本地 OpenAI 兼容 API

点击界面上的「启动 API」按钮，即可对外提供标准 RESTful 接口（默认监听 `http://127.0.0.1:21894`）。

### 接口定义：`POST /v1/chat/completions`

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

---

## 7. 从源码构建

### 环境要求
- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 支持 Vulkan 1.2+ 的显卡驱动（推荐更新至最新 NVIDIA / AMD / Intel 显卡驱动）

### 编译发布命令
```powershell
# 1. 克隆代码仓库（代码中不含模型大文件）
git clone git@github.com:zly258/OfflineTranslator.git
cd OfflineTranslator

# 2. 准备模型文件放入 models/ 目录
# 例如放入 models/Qwen/Qwen3-0.6B-Q4_K_M.gguf 与 models/Hy-MT2/Hy-MT2-1.8B-2Bit.gguf

# 3. 运行一键构建与自检脚本（发布至 dist/ 目录）
.\build.ps1
```

---

## 8. 许可证

本项目采用 [MIT License](LICENSE) 开源协议。
内部模型权重及推理引擎分别遵循原作者开源协议（Tencent Hunyuan License / Apache 2.0）。
