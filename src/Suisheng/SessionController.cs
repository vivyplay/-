using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Suisheng;

public enum SessionPhase { Idle, Preparing, Recording, Recognizing, Optimizing, AwaitingConfirmation, Writing, Downloading, Cancelling }

public sealed class SessionController : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly AudioRecorder recorder = new();
    private readonly RecognitionService recognizer = new();
    private readonly TargetInputService input = new();
    public HistoryStore History { get; } = new();
    public AiCredentialStore AiCredentials { get; } = new();
    public AiTemplateStore AiTemplates { get; } = new();
    private readonly OpenAiCompatibleClient aiClient = new();
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? finished;
    private InputTarget? target;
    private bool microphoneTest;
    public SettingsStore Store { get; } = new();
    public bool IsFirstRun => !Store.Exists && !Settings.OnboardingCompleted;
    public UserSettings Settings { get; private set; }
    public HotkeyService Hotkeys { get; } = new();
    public SessionPhase Phase { get; private set; }
    public bool Busy => Phase != SessionPhase.Idle;
    public string Status { get; private set; } = "待机";
    public string Detail { get; private set; } = "将光标放到输入框，按快捷键或点击悬浮球开始说话。";
    public string Result { get; private set; } = "";
    public string OriginalResult { get; private set; } = "";
    public double Level { get; private set; }
    public double Seconds { get; private set; }
    public double DownloadProgress { get; private set; }
    public event Action? Changed;
    public event Action? ShowRequested;
    public event Action<string>? SettingsApplied;
    public event Action<bool>? AiPanelRequested;

    public SessionController(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        Settings = Store.Load();
        Hotkeys.SetRecordingMode(Settings.RecordingMode == "hold");
        if (!Hotkeys.TrySet(Settings.HotkeyModifiers, Settings.HotkeyKey))
            Detail = "默认快捷键已被占用，请在设置中更换；仍可使用悬浮球。";
        else if (Store.LoadWarning != null) Detail = Store.LoadWarning;
        Hotkeys.Triggered += () => _ = ToggleAsync();
        Hotkeys.Released += () =>
        {
            if (Settings.RecordingMode != "hold") return;
            if (Phase == SessionPhase.Recording) _ = StopAsync();
            else if (Phase == SessionPhase.Preparing) _ = CancelAsync();
        };
        Hotkeys.Cancelled += () => _ = CancelAsync();
        Hotkeys.ShowRequested += () => ShowRequested?.Invoke();
        recorder.Meter += (level, seconds) => dispatcher.BeginInvoke(() =>
        {
            if (Phase != SessionPhase.Recording) return;
            Level = level * 100; Seconds = seconds; Changed?.Invoke();
        });
        recorder.Ended += error => dispatcher.BeginInvoke(() =>
        {
            if (Phase == SessionPhase.Recording) _ = StopAsync();
        });
    }

    public async Task ToggleAsync()
    {
        if (Phase == SessionPhase.Recording) { await StopAsync(); return; }
        if (Busy) return;
        await StartAsync(false);
    }

    public async Task TestMicrophoneAsync()
    {
        if (!Busy) await StartAsync(true);
    }

    private void Begin(SessionPhase phase, string status, string? detail = null)
    {
        cancellation = new CancellationTokenSource();
        finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPhase(phase, status, detail);
    }

    private async Task StartAsync(bool test)
    {
        Begin(SessionPhase.Preparing, "正在准备");
        microphoneTest = test;
        var token = cancellation!.Token;
        try
        {
            target = test ? null : await input.CaptureAsync();
            token.ThrowIfCancellationRequested();
            if (!test && !await RecognitionService.VerifyAsync(RecognitionService.ModelPath(Settings), ModelInfo.Find(Settings.ModelId), token))
                throw new InvalidDataException("请先在“模型与设置”中下载或导入已校验的模型。");
            token.ThrowIfCancellationRequested();
            Level = 0; Seconds = 0;
            recorder.Start(Settings.Microphone, test ? 10 : Settings.MaxRecordingSeconds, !test && Settings.SilenceDetection, Settings.SilenceWaitSeconds);
            bool escape = Hotkeys.SetTaskActive(true);
            string ending = Settings.RecordingMode == "hold" && !test ? "松开快捷键结束录音。" : "再次触发结束录音。";
            SetPhase(SessionPhase.Recording, test ? "麦克风测试" : "正在录音", test ? "测试最多 10 秒，不保存音频、不进行识别。" :
                (target == null ? "未确认输入框，识别完成后请复制结果。" : "请保持输入窗口和光标不变；" + ending) + (Settings.SilenceDetection && !test ? $" 检测到声音后连续静音 {Settings.SilenceWaitSeconds:0.#} 秒自动结束。" : "") + (escape ? " Esc 取消。" : " Esc 被占用，请用右键菜单取消。"));
        }
        catch (OperationCanceledException) { Finish("已取消", "本次任务已取消，未写入文字。"); }
        catch (Exception e) { Finish("无法开始", Friendly(e)); }
    }

    private async Task StopAsync()
    {
        if (Phase != SessionPhase.Recording) return;
        SetPhase(SessionPhase.Recognizing, microphoneTest ? "正在结束测试" : "正在识别", "正在本机 CPU 上处理；首次加载模型可能稍慢。Esc 可取消。");
        var token = cancellation!.Token;
        try
        {
            var audio = await recorder.StopAsync();
            recorder.Release();
            token.ThrowIfCancellationRequested();
            if (microphoneTest) { Finish("测试结束", audio.HasSound ? "检测到声音，麦克风可用。" : "未检测到明显声音，请检查麦克风。 "); return; }
            if (!audio.HasSound || audio.Samples.Length < 3200) { Finish("未检测到声音", "未生成或写入文字，请靠近麦克风重试。"); return; }
            cancellation.CancelAfter(TimeSpan.FromMinutes(3));
            string original = await recognizer.TranscribeAsync(audio.Samples, Settings, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(original)) { Finish("未识别到文字", "可尝试更换语言或使用 Base 模型。"); return; }
            string result = TextProcessingService.Process(original, Settings);
            if (string.IsNullOrWhiteSpace(result)) { Finish("处理后无文字", "原结果未自动写入；请检查可选的口语词清理设置。"); return; }
            OriginalResult = Settings.AiEnabled || Settings.KeepOriginalResult ? original : "";
            Result = result;
            if (Settings.AiEnabled)
            {
                SetPhase(SessionPhase.Optimizing, "正在 AI 优化", "仅将本次识别文字、优化要求和所选模板发送到你配置的 AI 服务；不发送音频。Esc 可取消。");
                AiPanelRequested?.Invoke(true);
                await OptimizeCoreAsync(result, token);
                return;
            }
            SetPhase(SessionPhase.Writing, "正在验证输入位置");
            var outcome = await input.WriteAsync(target, result, token);
            token.ThrowIfCancellationRequested();
            string historyWarning = RecordHistory(original, result, outcome.Submitted);
            Finish(outcome.Submitted ? "已提交输入" : "待手动输入", outcome.Message + (audio.EndedBySilence ? " 本次由静音检测结束录音。" : "") + historyWarning);
        }
        catch (OperationCanceledException) { Finish("已取消或超时", "任务已停止；没有继续提交输入。已产生的结果仍保留。 "); }
        catch (AiRequestException e) { Finish("AI 优化失败", e.Message + " 原文已保留，可复制、重试或关闭 AI 使用本地结果。"); }
        catch (Exception e) { Finish("处理失败", Friendly(e)); }
    }

    public void OpenAiPanel() => AiPanelRequested?.Invoke(false);

    public void SaveAiConfiguration(string endpoint, string model, int timeoutSeconds, string? newKey, bool clearKey)
    {
        if (Busy) throw new InvalidOperationException("请先结束或取消当前任务，再修改 AI 连接配置。");
        var next = Settings.Clone();
        next.AiEndpoint = endpoint.Trim(); next.AiModel = model.Trim(); next.AiTimeoutSeconds = timeoutSeconds;
        SettingsStore.Validate(next);
        if (next.AiEnabled && string.IsNullOrEmpty(newKey) && (clearKey || !AiCredentials.HasKey))
            throw new InvalidOperationException("AI 仍处于开启状态，请填写并保存 API Key，或先关闭 AI。");
        if (!string.IsNullOrEmpty(newKey)) AiCredentials.Save(newKey);
        else if (clearKey) AiCredentials.Clear();
        Store.Save(next); Settings = next;
        Notify("AI 配置已保存", AiCredentials.HasKey ? "接口和模型已保存；API Key 由当前 Windows 用户的 DPAPI 加密保护。" : "接口和模型已保存；尚未保存 API Key。");
    }

    public async Task SetAiEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            string? key = AiCredentials.Load();
            if (string.IsNullOrWhiteSpace(Settings.AiEndpoint) || string.IsNullOrWhiteSpace(Settings.AiModel) || string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("请先填写完整 Chat Completions 接口地址、模型名称和 API Key，再开启 AI。");
        }
        bool cancel = !enabled && Phase is SessionPhase.Optimizing or SessionPhase.AwaitingConfirmation;
        if (cancel) await CancelAsync();
        var next = Settings.Clone(); next.AiEnabled = enabled;
        SettingsStore.Validate(next); Store.Save(next); Settings = next;
        Notify(enabled ? "AI 优化已开启" : "AI 优化已关闭", enabled
            ? "之后的识别文字会在本地处理后发送到所配置服务，并先显示预览；不会发送音频或自动提交表单。"
            : "普通本地语音输入继续可用，不再发送 AI 优化请求。");
    }

    public void SaveAiPreferences(string instructions, string templateId, string strength, bool allowExpansion)
    {
        var next = Settings.Clone(); next.AiInstructions = instructions; next.AiTemplateId = templateId;
        next.AiStrength = strength; next.AiAllowExpansion = allowExpansion;
        SettingsStore.Validate(next); Store.Save(next); Settings = next;
    }

    private void SaveAiPreferences(string instructions, string templateId) =>
        SaveAiPreferences(instructions, templateId, Settings.AiStrength, Settings.AiAllowExpansion);

    public async Task OptimizeAsync(string original, string instructions, string templateId)
    {
        if (Busy) throw new InvalidOperationException("当前已有任务，请先完成或取消后再优化。");
        if (!Settings.AiEnabled) throw new InvalidOperationException("AI 优化当前关闭，原文不会发送。请先完成配置并开启 AI。");
        if (string.IsNullOrWhiteSpace(original) || original.Length > 100000) throw new ArgumentException("请输入 1–100000 字符的待优化原文。");
        SaveAiPreferences(instructions, templateId);
        OriginalResult = original; Result = original;
        Begin(SessionPhase.Optimizing, "正在 AI 优化", "仅发送面板中的原文、优化要求和所选模板；不发送音频。Esc 可取消。");
        AiPanelRequested?.Invoke(true);
        try { await OptimizeCoreAsync(original, cancellation!.Token); }
        catch (OperationCanceledException) { Finish("已取消 AI 优化", "请求已取消，迟到结果会被忽略；原文仍保留。 "); }
        catch (AiRequestException e) { Finish("AI 优化失败", e.Message + " 原文已保留，可复制、修改配置或重试。"); }
        catch (Exception e) { Finish("AI 优化失败", Friendly(e) + " 原文已保留。"); }
    }

    private async Task OptimizeCoreAsync(string source, CancellationToken token)
    {
        string key = AiCredentials.Load() ?? throw new AiRequestException(AiErrorKind.InvalidKey, "尚未配置 API Key。");
        var template = AiTemplates.Find(Settings.AiTemplateId);
        string optimized = await aiClient.OptimizeAsync(Settings, key, template, source, token);
        token.ThrowIfCancellationRequested();
        if (!Settings.AiEnabled || Phase != SessionPhase.Optimizing) throw new OperationCanceledException();
        Result = optimized;
        cancellation?.CancelAfter(Timeout.InfiniteTimeSpan);
        string historyWarning = TargetInputService.IsSafeText(optimized) ? RecordHistory(OriginalResult, optimized, false) : " 含换行等控制字符的结果未加入历史，可在面板复制。";
        SetPhase(SessionPhase.AwaitingConfirmation, "AI 结果待确认", "结果仅供预览，可编辑、复制、重新优化或使用原文。确认后需重新选择输入位置；不会执行 AI 返回的命令。" + historyWarning);
        AiPanelRequested?.Invoke(true);
    }

    public void UpdateAiResult(string edited)
    {
        if (Phase != SessionPhase.AwaitingConfirmation) return;
        if (string.IsNullOrWhiteSpace(edited) || edited.Length > 100000) throw new ArgumentException("优化结果需为 1–100000 字符；也可以取消并使用原文。");
        Result = edited; Changed?.Invoke();
    }

    public async Task ConfirmAiWriteAsync()
    {
        if (Phase != SessionPhase.AwaitingConfirmation || Result.Length == 0) return;
        Finish("已确认 AI 结果", "请重新选择输入位置，写入前会再次验证窗口、焦点和光标。");
        await RetryWriteAsync();
    }

    public async Task UseOriginalAiAsync()
    {
        if (Phase != SessionPhase.AwaitingConfirmation || OriginalResult.Length == 0) return;
        Result = OriginalResult;
        Finish("已选择原文", "请重新选择输入位置，写入前会再次验证窗口、焦点和光标。");
        await RetryWriteAsync();
    }

    public async Task TestAiConnectionAsync()
    {
        if (Busy) throw new InvalidOperationException("请先结束当前任务，再测试 AI 连接。");
        Begin(SessionPhase.Preparing, "正在测试 AI 连接", "将向你配置的服务发送一条极短测试请求，可能产生少量费用。不会自动重试。");
        try
        {
            string key = AiCredentials.Load() ?? throw new AiRequestException(AiErrorKind.InvalidKey, "尚未配置 API Key。");
            await aiClient.TestConnectionAsync(Settings, key, cancellation!.Token);
            Finish("AI 连接成功", "服务已按 OpenAI 兼容 Chat Completions 协议返回内容；这不代表后续优化结果已经确认或写入。");
        }
        catch (OperationCanceledException) { Finish("AI 连接测试已取消", "未继续等待服务响应，不会自动重试。"); }
        catch (AiRequestException e) { Finish("AI 连接失败", e.Message); }
        catch (Exception e) { Finish("AI 连接失败", Friendly(e)); }
    }

    public async Task CancelAsync()
    {
        if (!Busy) return;
        cancellation?.Cancel();
        if (Phase == SessionPhase.AwaitingConfirmation)
        {
            Finish("已取消 AI 预览", "未写入任何文字；当前原文和结果仍保留，可复制。");
            return;
        }
        if (Phase == SessionPhase.Recording) await StopAsync();
        else { Status = "正在取消"; Detail = "等待当前操作释放资源，不会写入迟到的结果。"; Changed?.Invoke(); }
        if (finished != null) await finished.Task;
    }

    public async Task DownloadAsync()
    {
        if (Busy) return;
        Begin(SessionPhase.Downloading, "正在下载模型");
        DownloadProgress = 0;
        try
        {
            await RecognitionService.DownloadAsync(Settings, new Progress<double>(value => { DownloadProgress = value; Changed?.Invoke(); }), cancellation!.Token);
            Finish("模型已就绪", "下载完成并通过 SHA-256 校验，可以开始离线语音输入。");
        }
        catch (OperationCanceledException) { Finish("下载已取消或超时", "未完成的临时文件已清理；可重新下载。"); }
        catch (Exception e) { Finish("下载失败", e is System.Net.Http.HttpRequestException ? "无法连接下载源，可手动选择镜像源重试，或导入模型。" : Friendly(e)); }
    }

    public async Task ImportModelAsync(string source)
    {
        if (Busy) return;
        Begin(SessionPhase.Preparing, "正在校验模型");
        string? temporary = null;
        try
        {
            var token = cancellation!.Token;
            if (!await RecognitionService.VerifyAsync(source, ModelInfo.Find(Settings.ModelId), token))
                throw new InvalidDataException("该文件与当前选定模型的大小或 SHA-256 不符。");
            Directory.CreateDirectory(Settings.ModelDirectory);
            string destination = RecognitionService.ModelPath(Settings);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                await using (var from = File.OpenRead(source))
                await using (var to = File.Create(temporary)) await from.CopyToAsync(to, token);
                if (!await RecognitionService.VerifyAsync(temporary, ModelInfo.Find(Settings.ModelId), token)) throw new InvalidDataException("复制后的模型校验失败。");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            Finish("模型已就绪", "模型已复制并通过 SHA-256 校验，原文件未修改。");
        }
        catch (OperationCanceledException) { Finish("导入已取消", "未使用未完成的模型文件。"); }
        catch (Exception e) { Finish("导入失败", Friendly(e)); }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task RetryWriteAsync()
    {
        if (Busy || Result.Length == 0) return;
        Begin(SessionPhase.Preparing, "请选择输入位置");
        Hotkeys.SetTaskActive(true);
        try
        {
            var token = cancellation!.Token;
            for (int seconds = 5; seconds > 0; seconds--)
            {
                Notify("请选择输入位置", $"{seconds} 秒后验证当前输入框，请切换窗口并放好光标。Esc 取消。");
                await Task.Delay(1000, token);
            }
            target = await input.CaptureAsync();
            token.ThrowIfCancellationRequested();
            SetPhase(SessionPhase.Writing, "正在验证输入位置");
            var outcome = await input.WriteAsync(target, Result, token);
            Finish(outcome.Submitted ? "已提交输入" : "待手动输入", outcome.Message);
        }
        catch (OperationCanceledException) { Finish("已取消", "没有继续写入文字。"); }
        catch (Exception e) { Finish("输入失败", Friendly(e)); }
    }

    public void CompleteOnboarding()
    {
        if (Busy) throw new InvalidOperationException("请等待当前任务结束后再关闭引导。");
        var settings = Settings.Clone();
        settings.OnboardingCompleted = true;
        Store.Save(settings);
        Settings = settings;
        Changed?.Invoke();
    }

    public void SaveSettings(UserSettings settings)
    {
        if (Busy) throw new InvalidOperationException("请等待当前任务结束后再保存设置。");
        SettingsStore.Validate(settings);
        var old = Settings;
        bool wasPaused = Hotkeys.Paused;
        if (!Hotkeys.TrySet(settings.HotkeyModifiers, settings.HotkeyKey)) throw new InvalidOperationException("快捷键已被占用，原设置保持不变。");
        try { Store.Save(settings); Settings = settings; Hotkeys.SetRecordingMode(settings.RecordingMode == "hold"); }
        catch { Hotkeys.TrySet(old.HotkeyModifiers, old.HotkeyKey); throw; }
        finally { if (wasPaused && !Hotkeys.Paused) Hotkeys.TogglePause(); }
        if (!settings.KeepOriginalResult) OriginalResult = "";
        Notify("设置已保存", "新设置已生效。");
        SettingsApplied?.Invoke(settings.Theme);
    }

    public void SavePosition(double left, double top)
    {
        Settings.BallLeft = left; Settings.BallTop = top;
        try { Store.Save(Settings); }
        catch (IOException) { Notify("位置未保存", "无法写入设置文件，下次启动将使用之前的位置。"); }
        catch (UnauthorizedAccessException) { Notify("位置未保存", "设置目录不可写。"); }
    }

    public void TogglePause()
    {
        if (Busy) { Notify(Status, "请先停止当前任务，再暂停快捷键。"); return; }
        bool success = Hotkeys.TogglePause();
        Notify(success ? (Hotkeys.Paused ? "快捷键已暂停" : "待机") : "恢复失败", success ? "悬浮球仍可用于开始录音。" : "快捷键被其他程序占用，请修改设置。");
    }

    public void ClearResult() { if (!Busy) { Result = ""; OriginalResult = ""; Changed?.Invoke(); } }
    public void DeleteHistory(Guid id) { History.Delete(id); Changed?.Invoke(); }
    public void ClearHistory() { History.Clear(); Changed?.Invoke(); }
    private string RecordHistory(string original, string result, bool submitted)
    {
        try { History.Record(Settings, original, result, submitted); return ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return " 本次结果未保存到历史，请检查本地数据目录。"; }
    }
    public void Notify(string status, string detail) { Status = status; Detail = detail; Changed?.Invoke(); }
    private void SetPhase(SessionPhase phase, string status, string? detail = null)
    { Phase = phase; Status = status; if (detail != null) Detail = detail; Changed?.Invoke(); }
    private void Finish(string status, string detail)
    {
        recorder.Release(); input.Forget(); target = null; Hotkeys.SetTaskActive(false);
        Level = 0;
        cancellation?.Dispose(); cancellation = null;
        Phase = SessionPhase.Idle; Status = status; Detail = detail;
        finished?.TrySetResult();
        Changed?.Invoke();
    }
    private static string Friendly(Exception e) => e switch
    {
        InvalidDataException or InvalidOperationException or AiRequestException => e.Message,
        UnauthorizedAccessException => "没有访问权限，请检查麦克风隐私设置和模型目录权限。",
        IOException => "无法读写文件，请检查目录权限、磁盘空间或模型是否被占用。",
        DllNotFoundException or TypeInitializationException => "语音运行库无法加载，请检查 x64 版本及 Microsoft Visual C++ 运行库。",
        _ => $"操作失败（{e.GetType().Name}）。结果不会丢失，请检查设备和模型后重试。"
    };
    public void Dispose() { recorder.Dispose(); input.Dispose(); Hotkeys.Dispose(); aiClient.Dispose(); cancellation?.Dispose(); }
}
