using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OfflineTranslator.Core;
using OfflineTranslator.Core.Models;

namespace OfflineTranslator.Wpf;

public partial class MainWindow : Window
{
    private readonly TranslationService _service = new();
    private CancellationTokenSource? _translateCts;
    private bool _busy;
    private bool _closing;
    private readonly System.Windows.Threading.DispatcherTimer _memoryTimer;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.Title;

        SourceLangCombo.ItemsSource = LanguageInfo.SourceLanguages;
        SourceLangCombo.SelectedIndex = 0; // "auto" 自动检测

        TargetLangCombo.ItemsSource = LanguageInfo.TargetLanguages;
        TargetLangCombo.SelectedIndex = 0; // "zh" 中文

        _service.Log += message => Dispatcher.BeginInvoke(() => AppendLog(message));
        ModelCombo.SelectionChanged += (_, _) => UpdateUiState();

        _memoryTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _memoryTimer.Tick += (_, _) => UpdateMemoryUsage();
        _memoryTimer.Start();
        UpdateMemoryUsage();

        UpdateUiState();
        Loaded += async (_, _) =>
        {
            AppendLog(AppInfo.Title);
            LoadGlossary();
            await RefreshModelsAsync(startup: true);
            await AutoLoadAsync();
        };
    }

    /// <summary>只读加载共享术语库（本程序不管理词条）。</summary>
    private void LoadGlossary()
    {
        try
        {
            var (count, errors) = _service.Glossary.Load();
            AppendLog($"术语库已加载：{count} 条" + (errors.Count > 0 ? $"（{errors.Count} 行格式异常已跳过）" : ""));
        }
        catch (Exception ex)
        {
            AppendLog("术语库加载失败（不影响翻译）：" + ex.Message);
        }
    }

    private void UpdateMemoryUsage()
    {
        using var process = Process.GetCurrentProcess();
        MemoryText.Text = $"{process.WorkingSet64 / 1024.0 / 1024.0:F0} MB";
    }

    private string ModelsDirectory => ModelScanner.GetDefaultModelsDirectory();

    // ============================ 模型管理 ============================

    private async Task RefreshModelsAsync(bool startup = false)
    {
        try
        {
            var models = _service.ScanModels(ModelsDirectory);
            var selected = ModelCombo.SelectedItem as ModelInfo;
            ModelCombo.ItemsSource = models;
            if (models.Count == 0)
            {
                ModelCombo.SelectedIndex = -1;
                if (startup) AppendLog($"未找到模型。请把 Hy-MT2 的 .gguf 放在：{ModelsDirectory}\\Hy-MT2");
            }
            else
            {
                var preferred = selected
                    ?? models.FirstOrDefault(m => m.FileName == Settings.LastModelFileName)
                    ?? models[0];
                ModelCombo.SelectedItem = models.Contains(preferred) ? preferred : models[0];
            }
        }
        catch (Exception ex)
        {
            AppendLog("扫描模型目录失败：" + ex.Message);
        }
        UpdateUiState();
        await Task.CompletedTask;
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        AppendLog("重新扫描 models 目录…");
        await RefreshModelsAsync();
        SetBusy(false);
    }

    private async void LoadModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not ModelInfo model)
        {
            AppendLog("请先在下拉框选择一个翻译模型");
            return;
        }

        SetBusy(true);
        try
        {
            await LoadSelectedModelAsync(model);
        }
        catch (Exception ex)
        {
            AppendLog("加载模型失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UnloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _service.UnloadModelAsync();
        }
        catch (Exception ex)
        {
            AppendLog("卸载模型失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ============================ API 服务 ============================

    private async void StartApiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            AppendLog("端口必须是 1-65535 之间的整数");
            return;
        }
        if (!_service.IsModelLoaded)
        {
            AppendLog("必须先加载模型才能启动 API 服务");
            return;
        }

        SetBusy(true);
        try
        {
            await _service.StartApiAsync(port);
        }
        catch (Exception ex)
        {
            AppendLog("启动 API 失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StopApiButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _service.StopApiAsync();
        }
        catch (Exception ex)
        {
            AppendLog("停止 API 失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopyApiUrlButton_Click(object sender, RoutedEventArgs e)
    {
        var url = _service.ApiUrl;
        if (!_service.IsApiRunning || string.IsNullOrEmpty(url)) return;
        try
        {
            Clipboard.SetText(url);
            AppendLog($"已复制 API 地址：{url}");
        }
        catch (Exception ex)
        {
            AppendLog("复制 API 地址失败：" + ex.Message);
        }
    }

    private async void TestApiButton_Click(object sender, RoutedEventArgs e)
    {
        var url = _service.ApiUrl;
        if (!_service.IsApiRunning || string.IsNullOrEmpty(url)) return;

        SetBusy(true);
        TestApiButton.Content = "测试中…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.BaseAddress = new Uri(url);

            // 1. /health 探活
            var health = await http.GetAsync("/health");
            if (!health.IsSuccessStatusCode)
            {
                AppendLog($"API 测试失败：/health 返回 {(int)health.StatusCode}");
                return;
            }

            // 2. 真实翻译请求（非流式），优先用输入框里的一句话作示例
            var sample = InputBox.Text.Trim() is { Length: > 0 } t && t.Length <= 200 ? t : "Hello, world!";
            var targetLang = (TargetLangCombo.SelectedItem as LanguageInfo)?.Code ?? "zh";
            var resp = await http.PostAsJsonAsync("/v1/chat/completions", new
            {
                model = _service.ModelName ?? "default",
                messages = new[] { new { role = "user", content = sample } },
                source_language = "auto",
                target_language = targetLang,
                stream = false,
            });
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                AppendLog($"API 测试失败：/v1/chat/completions 返回 {(int)resp.StatusCode}：{body}");
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            AppendLog($"API 测试通过 ✓  示例：\"{sample}\" → \"{content}\"");
        }
        catch (Exception ex)
        {
            AppendLog("API 测试失败：" + ex.Message);
        }
        finally
        {
            TestApiButton.Content = "测试 API";
            SetBusy(false);
        }
    }

    // ============================ 多语言翻译 ============================

    private void SwapLangButton_Click(object sender, RoutedEventArgs e)
    {
        var src = SourceLangCombo.SelectedItem as LanguageInfo;
        var tgt = TargetLangCombo.SelectedItem as LanguageInfo;
        if (tgt is null) return;

        if (src == LanguageInfo.Auto || src is null)
        {
            // 如果原本是自动检测，根据目标语言智能对调
            var newTarget = tgt.Code == "zh" ? "en" : "zh";
            var targetMatch = LanguageInfo.PopularLanguages.FirstOrDefault(l => l.Code == newTarget);
            var sourceMatch = LanguageInfo.SourceLanguages.FirstOrDefault(l => l.Code == tgt.Code);

            if (sourceMatch is not null) SourceLangCombo.SelectedItem = sourceMatch;
            if (targetMatch is not null) TargetLangCombo.SelectedItem = targetMatch;
        }
        else
        {
            var newSource = LanguageInfo.SourceLanguages.FirstOrDefault(l => l.Code == tgt.Code);
            var newTarget = LanguageInfo.TargetLanguages.FirstOrDefault(l => l.Code == src.Code);

            if (newSource is not null) SourceLangCombo.SelectedItem = newSource;
            if (newTarget is not null) TargetLangCombo.SelectedItem = newTarget;
        }

        // 同时互换输入框与输出框内容
        var input = InputBox.Text;
        var output = OutputBox.Text;
        if (!string.IsNullOrWhiteSpace(output))
        {
            InputBox.Text = output;
            OutputBox.Text = input;
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            if (TranslateButton.IsEnabled)
            {
                TranslateButton_Click(sender, e);
            }
        }
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            AppendLog("请先输入待翻译文本");
            return;
        }

        var srcLang = (SourceLangCombo.SelectedItem as LanguageInfo)?.Code ?? "auto";
        var tgtLang = (TargetLangCombo.SelectedItem as LanguageInfo)?.Code ?? "zh";

        // 如果是自动检测，实时探测并提示用户
        if (srcLang == "auto")
        {
            var detected = LanguageDetector.DetectLanguage(text);
            var detLangInfo = LanguageInfo.FromCode(detected);
            DetectedLangText.Text = $"[已识别: {detLangInfo.ChineseName}]";
            srcLang = detected;
        }
        else
        {
            DetectedLangText.Text = string.Empty;
        }

        // 如果源语言与目标语言相同，自动调整目标语言
        if (srcLang.Equals(tgtLang, StringComparison.OrdinalIgnoreCase))
        {
            tgtLang = srcLang == "zh" ? "en" : "zh";
            var autoTgt = LanguageInfo.TargetLanguages.FirstOrDefault(l => l.Code == tgtLang);
            if (autoTgt is not null) TargetLangCombo.SelectedItem = autoTgt;
        }

        var request = new TranslationRequest
        {
            Text = text,
            SourceLanguage = srcLang,
            TargetLanguage = tgtLang,
            MaxTokens = 512,
        };

        OutputBox.Clear();
        LatencyText.Text = "翻译中…";
        _translateCts = new CancellationTokenSource();
        SetBusy(true);
        var sw = Stopwatch.StartNew();

        try
        {
            long firstChunkMs = -1;
            var outputChars = 0;
            var updates = 0;
            await foreach (var chunk in _service.TranslateAsync(request, _translateCts.Token))
            {
                if (firstChunkMs < 0)
                {
                    firstChunkMs = sw.ElapsedMilliseconds;
                    LatencyText.Text = $"首段 {firstChunkMs} ms · 翻译中…";
                }

                OutputBox.AppendText(chunk);
                outputChars += chunk.Length;
                // 每 8 个分片滚动一次，避免每个 token 都触发布局。
                if (++updates % 8 == 0) OutputBox.ScrollToEnd();
            }

            OutputBox.ScrollToEnd();
            LatencyText.Text = $"耗时: {sw.ElapsedMilliseconds} ms";
            var first = firstChunkMs >= 0 ? firstChunkMs.ToString() : "--";
            AppendLog($"翻译完成：{text.Length} 字符 → {outputChars} 字符，方向 {srcLang} → {tgtLang}，首段 {first} ms，总耗时 {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            LatencyText.Text = "已取消";
            AppendLog("翻译已停止");
        }
        catch (ModelNotLoadedException ex)
        {
            LatencyText.Text = "未加载模型";
            AppendLog("错误：" + ex.Message);
        }
        catch (Exception ex)
        {
            LatencyText.Text = "翻译失败";
            AppendLog("翻译出错：" + ex.Message);
        }
        finally
        {
            _translateCts?.Dispose();
            _translateCts = null;
            SetBusy(false);
        }
    }

    private void CancelTranslateButton_Click(object sender, RoutedEventArgs e)
    {
        _translateCts?.Cancel();
        AppendLog("已请求停止翻译…");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        OutputBox.Clear();
        DetectedLangText.Text = string.Empty;
        LatencyText.Text = "耗时: -- ms";
    }

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var text = OutputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            AppendLog("已复制译文到剪贴板");
        }
        catch (Exception ex)
        {
            AppendLog("复制译文失败：" + ex.Message);
        }
    }

    private async Task AutoLoadAsync()
    {
        if (_service.IsModelLoaded || ModelCombo.SelectedItem is not ModelInfo model) return;
        SetBusy(true);
        try
        {
            await LoadSelectedModelAsync(model);
        }
        catch (Exception ex)
        {
            AppendLog("自动加载模型失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadSelectedModelAsync(ModelInfo model)
    {
        int? threads = null;
        if (int.TryParse(ThreadsBox.Text.Trim(), out var t) && t > 0)
            threads = t;

        var threadText = threads?.ToString() ?? "自动";
        AppendLog($"正在加载模型: {model.FileName}（线程: {threadText}）…");
        await _service.LoadModelAsync(new EngineModelSpec(model.Path, ModelName: model.FileName, Threads: threads));
        Settings.LastModelFileName = model.FileName;
    }

    // ============================ 状态与日志 ============================

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var loaded = _service.IsModelLoaded;
        var apiRunning = _service.IsApiRunning;
        var selectedModel = ModelCombo.SelectedItem as ModelInfo;

        var successBrush = TryFindResource("Brush.Success") as Brush ?? Brushes.Green;
        var disabledBrush = TryFindResource("Brush.Text.Disabled") as Brush ?? Brushes.Gray;

        SelectedModelText.Text = loaded ? _service.ModelName ?? "--" : selectedModel?.FileName ?? "--";

        ModelStatusText.Text = loaded ? "已就绪" : "未加载";
        ModelStatusText.Foreground = loaded ? successBrush : disabledBrush;

        var summary = _service.EngineRuntimeSummary;
        BackendStatusText.Text = loaded ? (summary ?? "就绪") : "--";
        BackendStatusText.ToolTip = loaded && !string.IsNullOrEmpty(summary) ? summary : "当前实际工作的推理后端（如 Vulkan GPU 加速、CPU 等）";

        ApiStatusText.Text = apiRunning ? $"运行中 ({_service.ApiUrl})" : "未运行";
        ApiStatusText.Foreground = apiRunning ? successBrush : disabledBrush;

        // 加载状态下保持下拉可用：选择其它模型再点「加载模型」即可热切换
        var switchingModel = loaded && selectedModel is not null
            && !string.Equals(selectedModel.FileName, _service.ModelName, StringComparison.OrdinalIgnoreCase);
        ModelCombo.IsEnabled = !_busy;
        RefreshModelsButton.IsEnabled = !_busy;
        LoadModelButton.IsEnabled = !_busy && selectedModel is not null && (!loaded || switchingModel);
        LoadModelButton.Content = loaded
            ? (switchingModel ? "切换到此模型" : "已加载")
            : "加载模型";
        UnloadModelButton.IsEnabled = !_busy && loaded;
        ThreadsBox.IsEnabled = !_busy && !loaded;

        PortBox.IsEnabled = !_busy && loaded && !apiRunning;
        StartApiButton.IsEnabled = !_busy && loaded && !apiRunning;
        StopApiButton.IsEnabled = !_busy && apiRunning;
        CopyApiUrlButton.IsEnabled = apiRunning;
        TestApiButton.IsEnabled = apiRunning && !_busy;

        TranslateButton.IsEnabled = !_busy && loaded;
        CancelTranslateButton.IsEnabled = _busy && _translateCts is not null;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        e.Cancel = true;
        IsEnabled = false;

        _memoryTimer.Stop();
        _translateCts?.Cancel();
        try
        {
            await _service.DisposeAsync();
        }
        catch { }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
