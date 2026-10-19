using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using OfflineTranslator.Core;

namespace OfflineTranslator.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // --autotest：免点击自动化自检（假引擎全链路），不初始化 WPF 消息循环与窗口
        if (e.Args.Any(a => a.Equals("--autotest", StringComparison.OrdinalIgnoreCase)))
        {
            int? port = null;
            for (var i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i].Equals("--port", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(e.Args[i + 1], out var p))
                {
                    port = p;
                }
            }

            var output = new StringBuilder();
            void Write(string s)
            {
                output.AppendLine(s);
                try { Console.WriteLine(s); } catch { /* WinExe 无控制台时忽略 */ }
            }

            int code;
            try
            {
                Write("== OfflineTranslator 自动化自检（--autotest）==");
                // 必须在后台线程跑：OnStartup 运行在 WPF 调度线程上，SynchronizationContext 会把
                // 自检里每个 await 的续体post 回调度线程，而这里又同步等待结果 → 经典死锁（自检永不返回）。
                code = Task.Run(() => SelfCheck.RunAsync(Write, port)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Write("自检进程异常：" + ex);
                code = 2;
            }

            try
            {
                var resultPath = Path.Combine(AppContext.BaseDirectory, "selfcheck-result.txt");
                File.WriteAllText(resultPath, output.ToString(), new UTF8Encoding(false));
            }
            catch { /* 写文件失败不影响退出码 */ }

            Environment.Exit(code);
            return;
        }

        if (e.Args.Any(a => a.Equals("--sample", StringComparison.OrdinalIgnoreCase)))
        {
            var output = new StringBuilder();
            void Write(string s)
            {
                output.AppendLine(s);
                try { Console.WriteLine(s); } catch { }
            }

            int code;
            try
            {
                code = Task.Run(() => RunSampleAsync(Write)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Write("样例翻译异常：" + ex);
                code = 2;
            }

            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sample-result.txt"), output.ToString(), new UTF8Encoding(false));
            }
            catch { }

            Environment.Exit(code);
            return;
        }

        if (e.Args.Any(a => a.Equals("--ui-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                base.OnStartup(e);
                var smokeWindow = new MainWindow();
                MainWindow = smokeWindow;
                smokeWindow.Show();
                smokeWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                smokeWindow.Close();
                Console.WriteLine("UI smoke test passed.");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("UI smoke test failed: " + ex);
                Environment.Exit(1);
            }
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static async Task<int> RunSampleAsync(Action<string> write)
    {
        await using var service = new TranslationService();
        var (_, errors) = service.Glossary.Load();
        write("术语库已加载" + (errors.Count == 0 ? "" : "，错误 " + errors.Count));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var modelsDir = ModelScanner.GetDefaultModelsDirectory();
        var model = service.ScanModels(modelsDir).FirstOrDefault(m => m.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("未找到 Hy-MT2 的 .gguf", modelsDir);
        await service.LoadModelAsync(new EngineModelSpec(model.Path, ModelName: model.FileName));
        write($"加载 {sw.ElapsedMilliseconds} ms");

        string[] samples =
        [
            "装配体中的零件需要做干涉检查。",
            "The flat pattern of the sheet metal part needs a new bend.",
        ];
        foreach (var text in samples)
        {
            sw.Restart();
            var sb = new StringBuilder();
            var src = text[0] > 127 ? "zh" : "en";
            var tgt = src == "zh" ? "en" : "zh";
            long first = -1;
            await foreach (var chunk in service.TranslateAsync(TranslationRequest.DefaultsFor(text, src, tgt)))
            {
                if (first < 0) first = sw.ElapsedMilliseconds;
                sb.Append(chunk);
            }
            write($"[{sw.ElapsedMilliseconds} ms，首段 {first} ms] {src}->{tgt}");
            write("  原文: " + text);
            write("  译文: " + sb);
        }
        return 0;
    }
}
