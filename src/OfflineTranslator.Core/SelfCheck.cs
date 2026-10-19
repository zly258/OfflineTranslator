using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OfflineTranslator.Core.Testing;

namespace OfflineTranslator.Core;

/// <summary>
/// 免点击自动化自检（--autotest）：
/// 使用假引擎在临时目录走完整链路 —— 模型扫描 → 加载/切换/卸载 → OpenAI API 启停 →
/// /health、/v1/models、非流式、SSE 流式、提示词支持(system prompt/术语传入)、参数覆盖、错误路径 —— 全部用真实 HTTP。
/// 返回退出码：0 = 全部通过。
/// </summary>
public static class SelfCheck
{
    public static async Task<int> RunAsync(Action<string> write, int? port = null, CancellationToken token = default)
    {
        var pass = 0;
        var fail = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            if (ok) pass++;
            else fail++;
            write($"[{(ok ? "PASS" : "FAIL")}] {name}" + (detail is null || ok ? "" : $"  —— {detail}"));
        }

        // 1. 临时 models 目录 + 假 ONNX 模型目录
        var modelsDir = Path.Combine(Path.GetTempPath(), "ot-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(modelsDir);
        var fakeModelPath = Path.Combine(modelsDir, "fake-model");
        Directory.CreateDirectory(fakeModelPath);
        await File.WriteAllTextAsync(Path.Combine(fakeModelPath, "encoder_model.onnx"), "fake onnx", token);
        write($"自检临时目录：{modelsDir}");

        // 1b. 术语库：临时目录隔离，不碰真实 %ProgramData%/%AppData%
        var glossaryDir = Path.Combine(modelsDir, "glossary");
        var glossaryUserDir = Path.Combine(modelsDir, "glossary-user");
        Directory.CreateDirectory(glossaryDir);
        Directory.CreateDirectory(glossaryUserDir);
        var canonicalCsv = Path.Combine(glossaryDir, "glossary.csv");
        var userCsv = Path.Combine(glossaryUserDir, "glossary.csv");
        GlossaryService.UsePathsForTest(canonicalCsv, userCsv);
        try
        {
            var glossary = new GlossaryService();

            // 规范文件不存在：内存中使用内置默认，不落盘
            var (loaded, loadErrors) = glossary.Load();
            Check("术语库内置默认条数 ≥ 50", loaded >= 50, $"实际 {loaded} 条");
            Check("规范文件缺失时不落盘", !File.Exists(canonicalCsv), string.Join("；", loadErrors));

            // L1 送翻前精准查库直出（Title Case 规范）
            Check("L1 装配体 → Assembly", glossary.TryTranslate("装配体", "zh", "en", out var t1) && t1 == "Assembly", t1);
            Check("L1 零件 → Part", glossary.TryTranslate("零件", "zh", "en", out var t2) && t2 == "Part", t2);
            Check("L1 工程图 → Drawing", glossary.TryTranslate("工程图", "zh", "en", out var t3) && t3 == "Drawing", t3);
            Check("L1 明细表 → Part List",
                glossary.TryTranslate("明细表", "zh", "en", out var t4) && t4 == "Part List", t4);
            Check("L1 未收录词条不误命中", !glossary.TryTranslate("未知的超级零件ABC", "zh", "en", out _));

            var matched = HyMtPrompt.MatchTerms("装配体中的零件需要干涉检查", glossary.Entries, "zh", "en");
            var prompt = HyMtPrompt.Build("装配体中的零件需要干涉检查", "en", matched);
            Check("术语提示含装配体", prompt.Contains("装配体 翻译成 Assembly", StringComparison.Ordinal), prompt);
            Check("术语提示含零件", prompt.Contains("零件 翻译成 Part", StringComparison.Ordinal));
            Check("术语提示含干涉检查", prompt.Contains("干涉检查 翻译成 Interference Check", StringComparison.Ordinal));
            Check("未出现的术语不注入", !prompt.Contains("工程图", StringComparison.Ordinal));
            var compound = HyMtPrompt.MatchTerms("装配约束",
                [new TermEntry("约束", "Constraint", "zh", "en"), new TermEntry("装配约束", "Assembly Constraint", "zh", "en")],
                "zh", "en");
            Check("短词不覆盖更长术语", compound.Count == 1 && compound[0].Source == "装配约束");
            var reversed = HyMtPrompt.MatchTerms("add a Mate constraint in the Assembly", glossary.Entries, "en", "zh");
            Check("反向术语 Assembly → 装配体", reversed.Any(t => t.Source == "Assembly" && t.Target == "装配体"));
            Check("反向术语 Mate → 配合", reversed.Any(t => t.Source.Equals("Mate", StringComparison.OrdinalIgnoreCase) && t.Target == "配合"));
            Check("目标语言写为英语", prompt.Contains("翻译为英语", StringComparison.Ordinal));
            var bare = HyMtPrompt.Build("今天天气不错", "ja", []);
            Check("无术语时不写参考译法", !bare.Contains("参考下面的翻译", StringComparison.Ordinal) && bare.Contains("翻译为日语", StringComparison.Ordinal));

            // 用户自定义提示词注入
            var withCustomPrompt = HyMtPrompt.Build("打开阀门", "en", [], "术语要求：阀门统一翻译成 Valve");
            Check("系统提示词/自定义术语支持", withCustomPrompt.Contains("术语要求：阀门统一翻译成 Valve", StringComparison.Ordinal));

            // 多模型提示词策略（IPromptFormatter）测试
            var qwenFormatter = PromptFormatterFactory.ForModel("qwen2.5-0.5b-instruct-q4_k_m.gguf");
            Check("Qwen 提示词策略路由", qwenFormatter is QwenPromptFormatter);
            var hyFormatter = PromptFormatterFactory.ForModel("models/Hy-MT2/Hy-MT2-1.8B-2Bit.gguf");
            Check("Hunyuan 提示词策略路由", hyFormatter is HunyuanPromptFormatter);
            var defaultFormatter = PromptFormatterFactory.ForModel("unknown-model.gguf");
            Check("默认提示词策略路由", defaultFormatter is DefaultPromptFormatter);

            var sampleTerms = new[] { new TermEntry("装配体", "Assembly", "zh", "en") };
            var qwenPrompt = qwenFormatter.BuildPrompt("检查装配体", "en", sampleTerms, "额外要求");
            Check("Qwen ChatML 包含 system 与 user",
                qwenPrompt.Contains("<|im_start|>system\n", StringComparison.Ordinal)
                && qwenPrompt.Contains("<|im_start|>user\n", StringComparison.Ordinal)
                && qwenPrompt.Contains("<|im_start|>assistant\n", StringComparison.Ordinal));
            Check("Qwen ChatML 包含术语与要求",
                qwenPrompt.Contains("装配体 => Assembly", StringComparison.Ordinal)
                && qwenPrompt.Contains("额外要求", StringComparison.Ordinal));

            // 思考过滤器（ThinkStreamFilter）测试：确保默认关闭并过滤所有思维链
            var filter = new ThinkStreamFilter();
            var outPieces = new StringBuilder();
            string[] rawChunks = ["<th", "ink>\n正在思考如何翻译...\n", "细节推敲</th", "ink>\n\nAssembly part"];
            foreach (var chunk in rawChunks) outPieces.Append(filter.Feed(chunk));
            outPieces.Append(filter.Flush());
            var filteredText = outPieces.ToString();
            Check("思考过程与<think>标签被完全剔除", !filteredText.Contains("<think") && !filteredText.Contains("正在思考") && filteredText.Contains("Assembly part"), filteredText);

            var directFilter = new ThinkStreamFilter();
            var directOut = directFilter.Feed("Normal translation result") + directFilter.Flush();
            Check("普通无思考文本不受影响原样输出", directOut == "Normal translation result", directOut);

            // 每用户覆盖优先于规范文件
            await File.WriteAllTextAsync(canonicalCsv,
                "Source,Target,SourceLang,TargetLang,CaseSensitive\n零件,Part,zh,en,0\n",
                new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(userCsv,
                "Source,Target,SourceLang,TargetLang,CaseSensitive\n零件,Component,zh,en,0\n个人补充词条,Personal Term,zh,en,0\n",
                new UTF8Encoding(false), token);
            var (loaded2, _) = glossary.Load();
            Check("术语库每用户覆盖优先",
                glossary.TryTranslate("零件", "zh", "en", out var t5) && t5 == "Component", t5);
            Check("术语库每用户覆盖新增词条",
                glossary.TryTranslate("个人补充词条", "zh", "en", out var t6) && t6 == "Personal Term", t6);
            Check("外部文件加载成功", loaded2 == 2, $"实际 {loaded2} 条");
        }
        finally
        {
            GlossaryService.UsePathsForTest(null, null);   // 恢复真实路径
        }

        // 1c. 翻译记忆：写入、跨实例载入、scope 隔离
        var tmPath = Path.Combine(modelsDir, "translation-memory.jsonl");
        var tmKey = TranslationMemory.ComputeKey("scope-a", "zh", "en", null, 512, "装配体");
        using (var tm = new TranslationMemory(tmPath))
        {
            tm.Put(tmKey, "Assembly");
            Check("翻译记忆写入", tm.TryGet(tmKey, out var v) && v == "Assembly", v);
        }
        using (var tm = new TranslationMemory(tmPath))
        {
            Check("翻译记忆重启后仍可用", tm.TryGet(tmKey, out var v) && v == "Assembly", v);
        }
        using (var tm = new TranslationMemory(tmPath))
        {
            var otherKey = TranslationMemory.ComputeKey("scope-b", "zh", "en", null, 512, "装配体");
            Check("翻译记忆按 scope 隔离", !tm.TryGet(otherKey, out _));
        }

        // 自检期间把服务级翻译记忆也隔离到临时目录，不碰用户真实记忆。
        var previousTmPath = Environment.GetEnvironmentVariable("WISDRI_TM_PATH");
        Environment.SetEnvironmentVariable("WISDRI_TM_PATH", Path.Combine(modelsDir, "service-tm.jsonl"));

        var service = new TranslationService
        {
            EngineFactory = (spec, _) =>
            {
                var fake = new FakeTranslationEngine();
                return fake.LoadAsync(spec, token).ContinueWith(_ => (ITranslationEngine)fake, token);
            },
        };
        service.Log += m => write("  [LOG] " + m);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string BaseUrl() => $"http://127.0.0.1:{service.ApiPort}";

        try
        {
            // 2. 扫描
            var models = service.ScanModels(modelsDir);
            Check("扫描 models 目录", models.Count == 1 && models[0].FileName == "fake-model");

            // 3. 未加载模型时禁止启动 API
            try
            {
                await service.StartApiAsync(port ?? 0, token);
                Check("未加载模型时禁止启动 API", false, "竟然启动成功了");
            }
            catch (ModelNotLoadedException)
            {
                Check("未加载模型时禁止启动 API", true);
            }

            // 4. 加载模型
            await service.LoadModelAsync(new EngineModelSpec(fakeModelPath), token);
            Check("加载模型", service.IsModelLoaded && service.ModelName == "fake-model");

            // 5. 启动 API（端口 0 = 自动分配空闲端口）
            var testPort = port ?? 0;
            await service.StartApiAsync(testPort, token);
            Check("启动 API 服务", service.IsApiRunning && service.ApiPort > 0);
            http.BaseAddress = new Uri(BaseUrl());

            // 6. GET /health
            var r0 = await http.GetAsync("/health", token);
            var j0 = await r0.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            Check("GET /health 探活", r0.IsSuccessStatusCode && j0.GetProperty("status").GetString() == "ok");

            // 7. GET /v1/models
            var r1 = await http.GetAsync("/v1/models", token);
            var j1 = await r1.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            var modelId = j1.GetProperty("data")[0].GetProperty("id").GetString();
            Check("GET /v1/models 返回模型名", r1.IsSuccessStatusCode && modelId == "fake-model");

            // 8. 非流式 chat completion
            var r2 = await http.PostAsJsonAsync("/v1/chat/completions",
                new { model = "fake", messages = new[] { new { role = "user", content = "Hello" } } }, token);
            var body2 = await r2.Content.ReadAsStringAsync(token);
            if (!r2.IsSuccessStatusCode)
            {
                Check("POST 非流式返回译文", false, $"HTTP {(int)r2.StatusCode}: {body2}");
            }
            else
            {
                using var doc2 = JsonDocument.Parse(body2);
                var content = doc2.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                Check("POST 非流式返回译文", !string.IsNullOrEmpty(content) && content!.Contains("Hello"),
                    content);
            }

            // 9. 流式（SSE）
            var r3 = await http.PostAsJsonAsync("/v1/chat/completions",
                new { model = "fake", stream = true, messages = new[] { new { role = "user", content = "Test" } } }, token);
            var sse = await r3.Content.ReadAsStringAsync(token);
            Check("POST 流式 SSE 帧",
                r3.IsSuccessStatusCode
                && r3.Content.Headers.ContentType!.MediaType == "text/event-stream"
                && sse.Contains("chat.completion.chunk")
                && sse.Contains("data: [DONE]"));

            // 10. 请求内携带提示词（system message 注入术语要求）与参数覆盖
            var r4 = await http.PostAsJsonAsync("/v1/chat/completions",
                new
                {
                    model = "fake",
                    messages = new[]
                    {
                        new { role = "system", content = "请将 Valve 翻译为阀门" },
                        new { role = "user", content = "Open the Valve" }
                    },
                    max_tokens = 42,
                    source_language = "en",
                    target_language = "zh",
                }, token);
            var last = FakeTranslationEngine.LastRequest;
            Check("API 接收 system 提示词",
                r4.IsSuccessStatusCode
                && last is not null
                && last.SystemPrompt == "请将 Valve 翻译为阀门"
                && last.Text == "Open the Valve");
            Check("API 请求内指定语言参数",
                last is not null
                && last.TargetLanguage == "zh"
                && last.SourceLanguage == "en"
                && last.MaxTokens == 42);

            // 11. 错误路径
            var r6 = await http.PostAsync("/v1/chat/completions",
                new StringContent("not-json", Encoding.UTF8, "application/json"), token);
            Check("非法 JSON → 400/500", !r6.IsSuccessStatusCode);

            var r7 = await http.PostAsJsonAsync("/v1/chat/completions", new { messages = Array.Empty<object>() }, token);
            Check("空 messages → 400", (int)r7.StatusCode == 400);

            // 12. 切换模型
            var fake2Path = Path.Combine(modelsDir, "fake-model-2");
            Directory.CreateDirectory(fake2Path);
            await File.WriteAllTextAsync(Path.Combine(fake2Path, "encoder_model.onnx"), "second fake model", token);
            await service.LoadModelAsync(new EngineModelSpec(fake2Path), token);
            Check("切换模型自动卸载旧模型(并停止 API)",
                service.IsModelLoaded && service.ModelName == "fake-model-2" && !service.IsApiRunning);

            // 13. 卸载模型 → API 自动停止
            await service.UnloadModelAsync(token);
            Check("卸载模型自动停止 API", !service.IsModelLoaded && !service.IsApiRunning);

            // 14. 卸载后调用 API 端口不再监听
            var stillUp = false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probe.CancelAfter(TimeSpan.FromMilliseconds(300));
                    _ = await http.GetAsync("/v1/models", probe.Token);
                    stillUp = true;
                    await Task.Delay(50, token);
                }
                catch (Exception)
                {
                    stillUp = false;
                    break;
                }
            }
            Check("卸载后 API 已下线", !stillUp, stillUp ? "仍然可以访问" : null);
        }
        catch (Exception ex)
        {
            fail++;
            write($"[FAIL] 自检异常 —— {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            await service.DisposeAsync();
            Environment.SetEnvironmentVariable("WISDRI_TM_PATH", previousTmPath);
            try { Directory.Delete(modelsDir, recursive: true); } catch { }
        }

        write($"自检完成：{pass} 项通过，{fail} 项失败");
        return fail == 0 ? 0 : 1;
    }
}
