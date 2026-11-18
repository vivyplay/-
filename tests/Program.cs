using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.Wave;
using Suisheng;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;

namespace SuishengTests;

public static class Program
{
    private static readonly List<object> results = [];
    private static int failures;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--host")) return RunHost(args[Array.IndexOf(args, "--host") + 1]);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
                int executableIndex = Array.IndexOf(args, "--executable");
                string? executable = executableIndex >= 0 ? args[executableIndex + 1] : null;
                string report = args.Contains("--desktop") ? (executable != null ? "desktop-release-results.json" : "desktop-results.json") : args.Contains("--notepad-write") ? (executable != null ? "notepad-write-release-results.json" : "notepad-write-results.json") : args.Contains("--ai-only") ? "ai-contract-results.json" : "test-results.json";
                File.WriteAllText(Path.Combine(root, report), JsonSerializer.Serialize(new[] { new { test = "完整测试运行", passed = false, detail = "测试进行中；若进程已退出，表示测试异常中断。" } }));
                if (args.Contains("--desktop")) await TestDesktop(root, executable, args.Contains("--ai-desktop-only"));
                else if (args.Contains("--notepad-write")) await TestNotepadWrite(root, executable);
                else if (args.Contains("--ai-only")) await Stage3Tests.Run(root);
                else await RunTests(root);
            }
            catch (Exception e) { Record("测试运行器", false, e.ToString()); code = 1; }
            finally
            {
                string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
                string report = args.Contains("--desktop") ? (args.Contains("--executable") ? "desktop-release-results.json" : "desktop-results.json") : args.Contains("--notepad-write") ? (args.Contains("--executable") ? "notepad-write-release-results.json" : "notepad-write-results.json") : args.Contains("--ai-only") ? "ai-contract-results.json" : "test-results.json";
                File.WriteAllText(Path.Combine(root, report), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                app.Shutdown(code != 0 || failures > 0 ? 1 : 0);
            }
        };
        return app.Run();
    }

    private static int RunHost(string output)
    {
        var app = new Application();
        var text = new TextBox { Name = "Target", Text = "seed", FontSize = 22, AcceptsReturn = true, Height = 130, Margin = new Thickness(20) };
        var password = new PasswordBox { Name = "Secret", Height = 40, Margin = new Thickness(20) };
        var readOnly = new TextBox { Name = "ReadOnlyTarget", Text = "不可修改", IsReadOnly = true, Height = 40, Margin = new Thickness(20) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = "随声输入自动化测试专用窗口", Margin = new Thickness(20) }); panel.Children.Add(text); panel.Children.Add(password); panel.Children.Add(readOnly);
        var window = new Window { Title = "Suisheng Isolated Input Test", Content = panel, Width = 620, Height = 450, Top = 80, Left = 80 };
        text.TextChanged += (_, _) => File.WriteAllText(output, text.Text, new System.Text.UTF8Encoding(false));
        window.Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; File.WriteAllText(output, text.Text); };
        return app.Run(window);
    }

    private static async Task RunTests(string root)
    {
        Directory.CreateDirectory(root);
        using (var voice = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            var format = new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono);
            foreach (var fixture in new[] { ("en-US", "english.wav", "Hello. This is a voice input test. The software converts speech into text."), ("zh-CN", "chinese.wav", "你好，这是一个语音输入测试。我们正在测试软件的本地识别功能。") })
            {
                var installed = voice.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Culture.Name == fixture.Item1);
                if (installed == null) continue;
                voice.SelectVoice(installed.VoiceInfo.Name);
                voice.SetOutputToWaveFile(Path.Combine(root, fixture.Item2), format);
                voice.Speak(fixture.Item3);
                voice.SetOutputToNull();
            }
        }
        string temporary = Path.Combine(root, "isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var settings = new UserSettings { ModelDirectory = root, ModelId = "tiny-q5_1" };
        var store = new SettingsStore(temporary);
        store.Save(settings); var loaded = store.Load();
        Record("设置原子保存与恢复", loaded.ModelDirectory == root && loaded.HotkeyKey == 32);
        try { SettingsStore.Validate(new() { MaxRecordingSeconds = -1 }); Record("非法设置拒绝", false); }
        catch (ArgumentException) { Record("非法设置拒绝", true); }
        File.WriteAllText(Path.Combine(temporary, "settings.json"), "not-json");
        Record("损坏设置保留并回退", new SettingsStore(temporary).Load().HotkeyKey == 32 && File.ReadAllText(Path.Combine(temporary, "settings.json")) == "not-json");
        Record("拒绝换行、Tab、空文本和超长文本", !TargetInputService.IsSafeText("hello\n") && !TargetInputService.IsSafeText("\t") && !TargetInputService.IsSafeText("") && !TargetInputService.IsSafeText(new string('a', 16001)));
        Record("中文英文和 Unicode 文本允许", TargetInputService.IsSafeText("你好，hello 123。"));
        Record("快捷键格式", HotkeyService.Format(3, 32) == "Ctrl + Alt + Space");
        using (var hotkey = new HotkeyService())
        {
            bool first = hotkey.TrySet(3, 0x78);
            using var other = new HotkeyService();
            bool conflict = !other.TrySet(3, 0x78);
            bool preserved = hotkey.TrySet(3, 0x78);
            Record("全局热键注册与冲突拒绝", first && conflict && preserved);
            bool changed = hotkey.TrySet(3, 0x79);
            bool oldReleased = other.TrySet(3, 0x78);
            using var replacementProbe = new HotkeyService();
            bool replacementConflict = !replacementProbe.TrySet(3, 0x79);
            Record("全局热键修改后旧键释放且新键生效", changed && oldReleased && replacementConflict);
            Record("暂停和恢复热键", hotkey.TogglePause() && hotkey.Paused && hotkey.TogglePause() && !hotkey.Paused);
        }
        Record("真实模型 SHA-256 校验", await RecognitionService.VerifyAsync(Path.Combine(root, "ggml-tiny-q5_1.bin"), ModelInfo.Find("tiny-q5_1"), CancellationToken.None));
        string invalid = Path.Combine(temporary, "invalid.bin"); File.WriteAllText(invalid, "invalid");
        Record("损坏模型拒绝", !await RecognitionService.VerifyAsync(invalid, ModelInfo.Find("tiny-q5_1"), CancellationToken.None));
        using (var file = File.Create(invalid)) file.SetLength(ModelInfo.Find("tiny-q5_1").Bytes);
        Record("大小正确但哈希错误的模型拒绝", !await RecognitionService.VerifyAsync(invalid, ModelInfo.Find("tiny-q5_1"), CancellationToken.None));
        Record("缺失模型拒绝", !await RecognitionService.VerifyAsync(Path.Combine(temporary, "missing.bin"), ModelInfo.Find("base"), CancellationToken.None));
        var recognition = new RecognitionService();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await recognition.TranscribeAsync(new float[16000], settings, cancelled.Token); Record("识别开始前取消", false); }
            catch (OperationCanceledException) { Record("识别开始前取消", true); }
        }
        foreach (var fixture in new[] { ("english.wav", "en", "voice"), ("chinese.wav", "zh", "语音") })
        {
            string wav = Path.Combine(root, fixture.Item1);
            if (!File.Exists(wav)) { Record("离线识别 " + fixture.Item2, false, "缺少合成语音测试文件"); continue; }
            using var reader = new WaveFileReader(wav);
            byte[] bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
            float[] samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            settings.Language = fixture.Item2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var clock = Stopwatch.StartNew();
            string result = await recognition.TranscribeAsync(samples, settings, timeout.Token);
            Record("本地 CPU 真实识别 " + fixture.Item2, result.Contains(fixture.Item3, StringComparison.OrdinalIgnoreCase), $"合成音频结果：{result}；耗时 {clock.Elapsed.TotalSeconds:F2}s");
        }
        if (File.Exists(Path.Combine(root, "ggml-base.bin")))
        {
            settings.ModelId = "base"; settings.Language = "zh";
            using var reader = new WaveFileReader(Path.Combine(root, "chinese.wav"));
            byte[] bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
            float[] samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, 2 * i) / 32768f;
            float[] longSamples = new float[16000 * 120];
            for (int offset = 0; offset < longSamples.Length; offset += samples.Length)
                Array.Copy(samples, 0, longSamples, offset, Math.Min(samples.Length, longSamples.Length - offset));
            for (int attempt = 1; attempt <= 3; attempt++)
            using (var runningCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var running = recognition.TranscribeAsync(longSamples, settings, runningCancellation.Token);
                await Task.Delay(500);
                bool wasRunning = !running.IsCompleted;
                using var queuedCancellation = new CancellationTokenSource();
                var queued = recognition.TranscribeAsync(samples, settings, queuedCancellation.Token);
                queuedCancellation.Cancel();
                try { await queued; Record($"排队中的识别可取消（第 {attempt} 次）", false); }
                catch (OperationCanceledException) { Record($"排队中的识别可取消（第 {attempt} 次）", wasRunning); }
                var clock = Stopwatch.StartNew();
                runningCancellation.Cancel();
                try { await running.WaitAsync(TimeSpan.FromSeconds(15)); Record($"识别任务启动后取消且不返回文本（第 {attempt} 次）", false); }
                catch (OperationCanceledException) { Record($"识别任务启动后取消且不返回文本（第 {attempt} 次）", wasRunning, $"取消等待 {clock.Elapsed.TotalSeconds:F2}s"); }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            string text = await recognition.TranscribeAsync(samples, settings, timeout.Token);
            Record("取消后仍可识别 Base 中文关键词", (text.Contains("语音输入") || text.Contains("語音輸入")) && (text.Contains("识别功能") || text.Contains("識別功能")), text);
        }
        else Record("Base 中文模型可用", false, "缺少 Base 模型，不能跳过默认模型测试");
        if (AudioRecorder.GetDevices().Count == 1)
        {
            using var recorder = new AudioRecorder();
            try { recorder.Start(-1, 5); Record("无麦克风明确拒绝", false); }
            catch (InvalidOperationException) { Record("无麦克风明确拒绝", true); }
        }
        await TestInput(temporary);
        await TestNotepadWrite(root);
        await Stage2Tests.Run(root);
        await Stage3Tests.Run(root);
        Console.WriteLine("测试结束，详见 test-results.json。");
    }

    private static async Task TestInput(string root)
    {
        string output = Path.Combine(root, "target.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--host"); start.ArgumentList.Add(output);
        using var host = Process.Start(start)!;
        try
        {
            for (int i = 0; i < 60 && !File.Exists(output); i++) await Task.Delay(100);
            host.Refresh(); nint handle = host.MainWindowHandle;
            if (handle == 0) throw new InvalidOperationException("测试输入窗口未启动");
            SetForegroundWindow(handle);
            await Task.Delay(300);
            var element = AutomationElement.FromHandle(handle);
            var text = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Target"));
            text.SetFocus(); await Task.Delay(200);
            using var input = new TargetInputService();
            var target = await input.CaptureAsync();
            Record("捕获真实外部输入框", target != null);
            uint clipboardVersion = GetClipboardSequenceNumber();
            var outcome = await input.WriteAsync(target, "你好 Voice 123", CancellationToken.None);
            await Task.Delay(250);
            string result = File.ReadAllText(output);
            Record("真实 Unicode 输入保留前文", outcome.Submitted && result == "seed你好 Voice 123", result + " / " + outcome.Message);
            Record("自动输入不修改剪贴板", GetClipboardSequenceNumber() == clipboardVersion);
            target = await input.CaptureAsync();
            var password = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "Secret"));
            password.SetFocus(); await Task.Delay(200);
            var blocked = await input.WriteAsync(target, "SHOULD_NOT_WRITE", CancellationToken.None);
            Record("焦点改变阻止写入", !blocked.Submitted && File.ReadAllText(output) == result);
            Record("密码框捕获被拒绝", await input.CaptureAsync() == null);
            var readOnly = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ReadOnlyTarget"));
            readOnly.SetFocus(); await Task.Delay(200);
            Record("只读输入框捕获被拒绝", await input.CaptureAsync() == null);
            text.SetFocus(); await Task.Delay(200);
            target = await input.CaptureAsync();
            password.SetFocus(); await Task.Delay(150);
            text.SetFocus(); await Task.Delay(150);
            var returned = await input.WriteAsync(target, "SHOULD_NOT_WRITE", CancellationToken.None);
            Record("切换焦点再返回也阻止旧目标写入", target != null && !returned.Submitted && File.ReadAllText(output) == result);
            target = await input.CaptureAsync();
            var newline = await input.WriteAsync(target, "do not submit\n", CancellationToken.None);
            Record("真实输入拒绝回车", !newline.Submitted && File.ReadAllText(output) == result);
            var range = ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.Clone();
            range.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, range, System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
            range.Select(); await Task.Delay(200);
            var moved = await input.WriteAsync(target, "SHOULD_NOT_WRITE", CancellationToken.None);
            Record("光标改变阻止写入", !moved.Submitted && File.ReadAllText(output) == result);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await input.WriteAsync(await input.CaptureAsync(), "SHOULD_NOT_WRITE", cancellation.Token); Record("取消写入", false); }
            catch (OperationCanceledException) { Record("取消写入", File.ReadAllText(output) == result); }
            var wholeText = ((TextPattern)text.GetCurrentPattern(TextPattern.Pattern)).DocumentRange;
            wholeText.Select(); await Task.Delay(150);
            target = await input.CaptureAsync();
            string replacement = "选中内容替换 Voice \U00020000";
            var selected = await input.WriteAsync(target, replacement, CancellationToken.None);
            await Task.Delay(200);
            Record("选区替换及补充平面 Unicode 写入", selected.Submitted && File.ReadAllText(output) == replacement);
            target = await input.CaptureAsync();
            host.CloseMainWindow(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var closed = await input.WriteAsync(target, "SHOULD_NOT_WRITE", CancellationToken.None);
            Record("目标关闭阻止写入", target != null && !closed.Submitted);
        }
        finally
        {
            if (!host.HasExited) { host.CloseMainWindow(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
    private static async Task TestNotepadWrite(string root, string? executable = null)
    {
        string path = Path.Combine(root, "notepad-write-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        AutomationElement? window = null;
        for (int i = 0; i < 60 && window == null; i++)
        {
            await Task.Delay(100);
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            foreach (AutomationElement candidate in windows)
                if (candidate.Current.Name.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) { window = candidate; break; }
        }
        if (window == null) { Record("临时记事本窗口启动", false); return; }
        nint handle = (nint)window.Current.NativeWindowHandle;
        for (int i = 0; i < 20 && NativeMethods.GetForegroundWindow() != handle; i++)
        {
            SetForegroundWindow(handle);
            await Task.Delay(100);
        }
        if (NativeMethods.GetForegroundWindow() != handle)
        {
            NativeMethods.SetWindowPos(handle, -1, 0, 0, 0, 0, 0x53);
            var rect = window.Current.BoundingRectangle;
            var point = new NativePoint { X = (int)(rect.X + rect.Width / 2), Y = (int)(rect.Y + rect.Height / 2) };
            nint atPoint = WindowFromPoint(point);
            if (GetAncestor(atPoint, 2) != handle)
            {
                NativeMethods.SetWindowPos(handle, -2, 0, 0, 0, 0, 0x53);
                Record("临时记事本点击区域验证", false);
                return;
            }
            SetCursorPos(point.X, point.Y);
            mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
            NativeMethods.SetWindowPos(handle, -2, 0, 0, 0, 0, 0x53);
            await Task.Delay(300);
        }
        if (NativeMethods.GetForegroundWindow() != handle) { Record("临时记事本置于前台", false); return; }
        string assemblyPath = executable == null ? typeof(TargetInputService).Assembly.Location : Path.ChangeExtension(Path.GetFullPath(executable), ".dll")!;
        IDisposable input;
        if (executable == null) input = new TargetInputService();
        else
        {
            var loadContext = new AssemblyLoadContext("Release notepad validation");
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            input = (IDisposable)Activator.CreateInstance(assembly.GetType("Suisheng.TargetInputService", true)!)!;
        }
        using (input)
        {
            object? target = await InvokeInputAsync(input, "CaptureAsync");
            nint targetWindow = target == null ? 0 : (nint)target.GetType().GetProperty("Window")!.GetValue(target)!;
            Record("临时记事本目标捕获", target != null && targetWindow == handle);
            if (target == null) return;
            const string text = "1. 明天上午十点召开产品会议；2. 讨论项目进度和目前存在的问题；3. 确定后续工作安排。";
            object outcome = (await InvokeInputAsync(input, "WriteAsync", target, text, CancellationToken.None))!;
            bool submitted = (bool)outcome.GetType().GetProperty("Submitted")!.GetValue(outcome)!;
            await Task.Delay(500);
            var element = (AutomationElement)target.GetType().GetProperty("Element")!.GetValue(target)!;
            string actual = ((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1).TrimEnd('\r', '\n');
            Record("记事本完整 Unicode 实际接收", submitted && actual == text,
                $"提交={submitted}；预期={text}；实际={actual}；文件={path}；程序集={assemblyPath}");
        }
    }

    private static async Task<object?> InvokeInputAsync(object input, string method, params object?[] arguments)
    {
        var task = (Task)input.GetType().GetMethod(method)!.Invoke(input, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static async Task TestDesktop(string root, string? executableOverride, bool aiOnly = false)
    {
        string executable = Path.GetFullPath(executableOverride ?? Path.Combine(root, "../src/Suisheng/bin/Debug/net10.0-windows/win-x64/Suisheng.exe"));
        string screenshotPrefix = executableOverride == null ? "stage3" : "stage3-release";
        if (Process.GetProcessesByName("Suisheng").Length != 0)
            throw new InvalidOperationException("请先手动退出正在运行的随声输入；测试不会操作已有实例。");
        string localRoot = Path.Combine(root, "desktop-data-" + Guid.NewGuid().ToString("N"));
        string dataDirectory = Path.Combine(localRoot, "Suisheng");
        Directory.CreateDirectory(dataDirectory);
        var history = new[]
        {
            new HistoryEntry(Guid.NewGuid(), DateTime.Now, "桌面原文一", "桌面历史一", true),
            new HistoryEntry(Guid.NewGuid(), DateTime.Now.AddMinutes(-1), "桌面原文二", "桌面历史二", false)
        };
        File.WriteAllText(Path.Combine(dataDirectory, "history.json"), JsonSerializer.Serialize(history));
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.Environment["SUISHENG_DATA_DIRECTORY"] = dataDirectory;
        using var process = Process.Start(startInfo)!;
        try
        {
            AutomationElement? panel = null;
            for (int i = 0; i < 80; i++)
            {
                await Task.Delay(100);
                if (process.HasExited) throw new InvalidOperationException("桌面程序提前退出");
                panel = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.NameProperty, "随声输入")));
                if (panel != null) break;
            }
            if (panel == null) throw new InvalidOperationException("找不到桌面主面板");
            Record("桌面程序实际启动", true);
            AutomationElement Find(string id) => panel.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            Record("启动后待机而非录音", Find("StatusText").Current.Name == "待机");
            Record("无结果时复制、重写及取消按钮禁用", !Find("CopyButton").Current.IsEnabled && !Find("RetryButton").Current.IsEnabled && !Find("CancelButton").Current.IsEnabled);
            var ball = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.NameProperty, "随声输入 · 悬浮球")));
            Record("悬浮球存在且置顶", ball != null && (GetWindowLongPtr((nint)ball.Current.NativeWindowHandle, -20).ToInt64() & 0x8) != 0);
            if (ball == null) throw new InvalidOperationException("找不到悬浮球");
            panel.SetFocus(); await Task.Delay(250);
            var rect = panel.Current.BoundingRectangle;
            using (var image = new System.Drawing.Bitmap((int)rect.Width, (int)rect.Height))
            {
                using var graphics = System.Drawing.Graphics.FromImage(image);
                graphics.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, image.Size);
                image.Save(Path.Combine(root, screenshotPrefix + "-main.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var settingsTab = panel.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem), new PropertyCondition(AutomationElement.NameProperty, "模型与录音")));
            ((SelectionItemPattern)settingsTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); await Task.Delay(150);
            Record("模型与设置页面可打开", Find("ModelDirectoryBox") != null && Find("SaveButton").Current.IsEnabled);
            if (executableOverride != null)
            {
                string modelDirectory = ((ValuePattern)Find("ModelDirectoryBox").GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                var selection = ((SelectionPattern)Find("ModelCombo").GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
                Record("发布版默认选择 Base 模型", selection.Length == 1 && selection[0].Current.Name.Contains("Base"));
                Record("发布版模型目录中的 Base 校验通过", await RecognitionService.VerifyAsync(Path.Combine(modelDirectory, "ggml-base.bin"), ModelInfo.Find("base"), CancellationToken.None));
            }
            using (var image = new System.Drawing.Bitmap((int)rect.Width, (int)rect.Height))
            {
                using var graphics = System.Drawing.Graphics.FromImage(image);
                graphics.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, image.Size);
                image.Save(Path.Combine(root, screenshotPrefix + "-settings.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            if (!aiOnly) await Stage2DesktopTests.Run(panel, ball, process, root, dataDirectory, screenshotPrefix);
            await Stage3DesktopTests.Run(panel, ball, process, root, dataDirectory, screenshotPrefix);
            var secondStart = new ProcessStartInfo(executable) { UseShellExecute = false };
            secondStart.Environment["SUISHENG_DATA_DIRECTORY"] = dataDirectory;
            using var second = Process.Start(secondStart)!;
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Record("重复启动仅保留原实例", !process.HasExited && second.ExitCode == 0);
            ((WindowPattern)panel.GetCurrentPattern(WindowPattern.Pattern)).Close(); await Task.Delay(200);
            Record("关闭面板后进程和悬浮球保留", !process.HasExited && IsWindow((nint)ball!.Current.NativeWindowHandle));
            var ballRect = ball!.Current.BoundingRectangle;
            SetCursorPos((int)(ballRect.X + ballRect.Width / 2), (int)(ballRect.Y + ballRect.Height / 2));
            mouse_event(0x0008, 0, 0, 0, 0); mouse_event(0x0010, 0, 0, 0, 0); await Task.Delay(200);
            var menus = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
            AutomationElement? exit = null;
            foreach (AutomationElement window in menus)
            {
                exit = window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem), new PropertyCondition(AutomationElement.NameProperty, "退出软件")));
                if (exit != null) break;
            }
            Record("悬浮球右键菜单可打开", exit != null);
            if (exit != null)
            {
                ((InvokePattern)exit.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                Record("右键退出释放进程", process.ExitCode == 0);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    internal static void Record(string test, bool passed, string? detail = null)
    {
        if (!passed) failures++;
        results.Add(new { test, passed, detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {test} {detail}");
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
