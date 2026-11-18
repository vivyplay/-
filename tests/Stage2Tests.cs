using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Suisheng;
using Drawing = System.Drawing;

namespace SuishengTests;

internal static class Stage2Tests
{
    public static async Task Run(string root)
    {
        string temporary = Path.Combine(root, "stage2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        await TestHotkeys();
        TestSilence();
        TestTextProcessing();
        TestSettings(temporary);
        TestHistory(temporary);
        TestAppearance(temporary);
    }

    private static async Task TestHotkeys()
    {
        var states = new Dictionary<int, short>();
        int reads = 0;
        short ReadKey(int virtualKey)
        {
            reads++;
            return states.TryGetValue(virtualKey, out short state) ? state : (short)0;
        }
        using (var hotkey = new HotkeyService(ReadKey))
        {
            int triggered = 0, released = 0;
            hotkey.Triggered += () => { triggered++; hotkey.SetTaskActive(true); };
            hotkey.Released += () => released++;
            bool registered = hotkey.TrySet(3, 0x78);
            hotkey.SetRecordingMode(true);
            hotkey.SetTaskActive(true);
            await Task.Delay(140);
            Program.Record("按住说话未触发时不轮询或误停", registered && triggered == 0 && released == 0 && reads == 0);

            states[0x11] = states[0x12] = states[0x78] = unchecked((short)0x8000);
            var source = (HwndSource)typeof(HotkeyService).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hotkey)!;
            NativeMethods.PostMessage(source.Handle, NativeMethods.WM_HOTKEY, 102, 0);
            bool triggerArrived = await WaitUntil(() => triggered == 1);
            for (int i = 0; i < 6; i++) NativeMethods.PostMessage(source.Handle, NativeMethods.WM_HOTKEY, 102, 0);
            hotkey.SetTaskActive(false);
            await Task.Delay(140);
            bool repeatIgnored = triggered == 1 && released == 0 && reads > 0;
            states[0x11] = states[0x12] = states[0x78] = 0;
            bool releaseArrived = await WaitUntil(() => released == 1);
            await Task.Delay(140);
            Program.Record("按住说话重复消息及任务结束不重复触发", triggerArrived && repeatIgnored && releaseArrived && triggered == 1 && released == 1);

            int stoppedReads = reads;
            await Task.Delay(140);
            Program.Record("按住说话松键后停止轮询", reads == stoppedReads && released == 1);
        }

        using (var hotkey = new HotkeyService())
        {
            int triggered = 0, released = 0;
            hotkey.Triggered += () => { triggered++; hotkey.SetTaskActive(true); };
            hotkey.Released += () => released++;
            bool registered = hotkey.TrySet(3, 0x79);
            hotkey.SetRecordingMode(true);
            bool sent = false;
            try
            {
                sent = SendKeys((0x11, false), (0x12, false), (0x79, false));
                bool triggerArrived = await WaitUntil(() => triggered == 1);
                await Task.Delay(400);
                SendKeys((0x79, true), (0x12, true), (0x11, true));
                bool releaseArrived = await WaitUntil(() => released == 1);
                await Task.Delay(120);
                Program.Record("真实全局按键长按不重复触发且松键结束一次", registered && sent && triggerArrived && releaseArrived && triggered == 1 && released == 1);
            }
            finally
            {
                if (sent) SendKeys((0x79, true), (0x12, true), (0x11, true));
            }
        }
        Program.Record("休眠及唤醒均要求取消活动任务", App.RequiresTaskCancellation(PowerModes.Suspend) && App.RequiresTaskCancellation(PowerModes.Resume) && !App.RequiresTaskCancellation(PowerModes.StatusChange));
    }

    private static void TestSilence()
    {
        var silence = new SilenceTracker();
        bool quietBeforeSound = !silence.Update(0, 5, 2) && !silence.Update(0.01, 5, 2);
        silence.Update(0.04, 0.05, 2);
        bool beforeBoundary = !silence.Update(0, 1.94, 2) && !silence.Update(0, 0.05, 2);
        bool atBoundary = silence.Update(0, 0.01, 2);
        Program.Record("静音检测未出声不提前结束", quietBeforeSound);
        Program.Record("静音检测声音后到达等待边界才结束", beforeBoundary && atBoundary);
        Program.Record("最长录音时长边界独立有效", !AudioRecorder.ReachedMaximum(119.99, 120) && AudioRecorder.ReachedMaximum(120, 120) && AudioRecorder.ReachedMaximum(121, 120));
    }

    private static void TestTextProcessing()
    {
        var settings = new UserSettings { AutoPunctuation = false, BasicFormatting = false, ReplacementRules = "AI => 机器\nAI 助手 => 小随" };
        Program.Record("词语替换最长优先且不级联", TextProcessingService.Process("AI 助手和AI", settings) == "小随和机器");
        settings.ReplacementRules = "OpenAI => 小随";
        Program.Record("词语替换忽略大小写", TextProcessingService.Process("openai OPENAI OpenAi", settings) == "小随 小随 小随");
        settings.ReplacementRules = "A => $1\\literal";
        Program.Record("替换目标按字面文本处理", TextProcessingService.Process("a", settings) == "$1\\literal");
        string[] invalidRules = ["缺少分隔符", "=> 空原词", "a => 1 => 2", "A => 1\na => 2", "a => 1\na => 2"];
        Program.Record("非法及重复替换规则拒绝", invalidRules.All(rule => Rejects(() => TextProcessingService.ParseReplacements(rule))) && Rejects(() => TextProcessingService.ParseReplacements(string.Join("\n", Enumerable.Range(0, 201).Select(i => $"词{i} => 值")))));
        settings.ReplacementRules = "";
        settings.RemoveFillerWords = false;
        Program.Record("口语词清理默认关闭", !new UserSettings().RemoveFillerWords && TextProcessingService.Process("hello um 嗯 world", settings) == "hello um 嗯 world");
        settings.RemoveFillerWords = true;
        Program.Record("可选口语词只清理分隔词", TextProcessingService.Process("hello um 嗯 world summary", settings) == "hello world summary");
        settings.RemoveFillerWords = false;
        settings.BasicFormatting = true;
        Program.Record("基础排版合并空格与 Tab", TextProcessingService.Process("  a  b\t\tc  ", settings) == "a b c");
        settings.AutoPunctuation = true; settings.Language = "zh";
        bool chinese = TextProcessingService.Process("你好", settings) == "你好。" && TextProcessingService.Process("你好！", settings) == "你好！";
        settings.Language = "en";
        bool english = TextProcessingService.Process("Hello", settings) == "Hello.";
        settings.AutoPunctuation = false;
        Program.Record("自动标点与空结果边界", chinese && english && TextProcessingService.Process("Hello", settings) == "Hello" && TextProcessingService.Process("  ", settings) == "");
        Program.Record("文字处理拒绝控制字符和超长结果", Rejects(() => TextProcessingService.Process("hello\nworld", settings)) && Rejects(() => TextProcessingService.Process(new string('a', 16001), settings)));
    }

    private static void TestSettings(string root)
    {
        string directory = Path.Combine(root, "settings"); Directory.CreateDirectory(directory);
        string legacyPath = Path.Combine(directory, "settings.json");
        var legacy = new Dictionary<string, object>
        {
            ["HotkeyModifiers"] = 3, ["HotkeyKey"] = 32, ["Microphone"] = -1,
            ["Language"] = "zh", ["ModelDirectory"] = root, ["ModelId"] = "base",
            ["DownloadSource"] = "official", ["MaxRecordingSeconds"] = 120
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));
        var old = new SettingsStore(directory).Load();
        Program.Record("旧设置 JSON 使用第二阶段安全缺省值", old.RecordingMode == "toggle" && !old.SilenceDetection && old.SilenceWaitSeconds == 2 && old.AutoPunctuation && old.BasicFormatting && !old.RemoveFillerWords && old.KeepOriginalResult && old.Theme == "light" && old.OrbStyle == "wave" && old.AvatarZoom == 1 && old.BallSize == 82 && old.BallOpacity == 1 && !old.HistoryEnabled && old.HistoryRetentionDays == 30);

        var all = new UserSettings
        {
            ModelDirectory = root, RecordingMode = "hold", SilenceDetection = true, SilenceWaitSeconds = 3.5,
            AutoPunctuation = false, BasicFormatting = false, RemoveFillerWords = true,
            ReplacementRules = "随声 => 随声输入", KeepOriginalResult = false, Theme = "dark", OrbStyle = "chat",
            AvatarSourcePath = Path.Combine(root, "source.png"), OrbImagePath = Path.Combine(root, "orb.png"),
            AvatarZoom = 2.5, AvatarOffsetX = 0.4, AvatarOffsetY = -0.3, BallSize = 120, BallOpacity = 0.65,
            HistoryEnabled = true, HistoryRetentionDays = 90
        };
        var store = new SettingsStore(directory); store.Save(all); var loaded = store.Load();
        Program.Record("第二阶段全部新设置保存恢复", loaded.RecordingMode == "hold" && loaded.SilenceDetection && loaded.SilenceWaitSeconds == 3.5 && !loaded.AutoPunctuation && !loaded.BasicFormatting && loaded.RemoveFillerWords && loaded.ReplacementRules == all.ReplacementRules && !loaded.KeepOriginalResult && loaded.Theme == "dark" && loaded.OrbStyle == "chat" && loaded.AvatarSourcePath == all.AvatarSourcePath && loaded.OrbImagePath == all.OrbImagePath && loaded.AvatarZoom == 2.5 && loaded.AvatarOffsetX == 0.4 && loaded.AvatarOffsetY == -0.3 && loaded.BallSize == 120 && loaded.BallOpacity == 0.65 && loaded.HistoryEnabled && loaded.HistoryRetentionDays == 90);

        var invalid = new (string Name, UserSettings Settings)[]
        {
            ("录音模式", new() { RecordingMode = "unknown" }), ("静音下限", new() { SilenceWaitSeconds = 0.49 }),
            ("静音上限", new() { SilenceWaitSeconds = 10.01 }), ("静音 NaN", new() { SilenceWaitSeconds = double.NaN }),
            ("主题", new() { Theme = "blue" }), ("形象", new() { OrbStyle = "gif" }),
            ("缩放下限", new() { AvatarZoom = 0.99 }), ("缩放上限", new() { AvatarZoom = 4.01 }),
            ("横向偏移", new() { AvatarOffsetX = 1.01 }), ("纵向偏移", new() { AvatarOffsetY = double.PositiveInfinity }),
            ("悬浮球大小", new() { BallSize = 47 }), ("悬浮球透明度", new() { BallOpacity = 0.39 }),
            ("历史期限下限", new() { HistoryRetentionDays = 0 }), ("历史期限上限", new() { HistoryRetentionDays = 3651 }),
            ("图片相对路径", new() { OrbImagePath = "relative.png" }), ("原图相对路径", new() { AvatarSourcePath = "relative.png" }),
            ("非法替换", new() { ReplacementRules = "A => 1\na => 2" })
        };
        string[] rejected = invalid.Where(item => !Rejects(() => SettingsStore.Validate(item.Settings))).Select(item => item.Name).ToArray();
        Program.Record("第二阶段新设置非法范围全部拒绝", rejected.Length == 0, rejected.Length == 0 ? null : "未拒绝：" + string.Join("、", rejected));

        string original = File.ReadAllText(legacyPath);
        File.WriteAllText(legacyPath, original.Replace("\"MaxRecordingSeconds\": 120", "\"HistoryRetentionDays\": 0"));
        string damaged = File.ReadAllText(legacyPath);
        var fallbackStore = new SettingsStore(directory); var fallback = fallbackStore.Load();
        Program.Record("非法新设置保留文件并回退", fallback.HistoryRetentionDays == 30 && fallbackStore.LoadWarning != null && File.ReadAllText(legacyPath) == damaged);
    }

    private static void TestHistory(string root)
    {
        string directory = Path.Combine(root, "history"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "history.json");
        var settings = new UserSettings { ModelDirectory = root };
        var store = new HistoryStore(directory);
        store.Record(settings, "原文", "结果", true);
        Program.Record("历史默认关闭且不创建文件", !settings.HistoryEnabled && !File.Exists(path) && store.GetAll(settings).Count == 0);

        settings.HistoryEnabled = true;
        store.Record(settings, "原文", "结果", true);
        var entries = store.GetAll(settings);
        Program.Record("历史主动开启后新增及原文保存", entries.Count == 1 && entries[0].Original == "原文" && entries[0].Text == "结果" && entries[0].Submitted && File.Exists(path));

        string expiryDirectory = Path.Combine(root, "history-expiry"); Directory.CreateDirectory(expiryDirectory);
        var oldEntry = new HistoryEntry(Guid.NewGuid(), DateTime.Now.AddDays(-10), "旧原文", "旧结果", false);
        var newEntry = new HistoryEntry(Guid.NewGuid(), DateTime.Now.AddDays(-1), "新原文", "新结果", true);
        File.WriteAllText(Path.Combine(expiryDirectory, "history.json"), JsonSerializer.Serialize(new[] { oldEntry, newEntry }));
        settings.HistoryRetentionDays = 3;
        var unexpired = new HistoryStore(expiryDirectory).GetAll(settings);
        Program.Record("历史保留期限过滤", unexpired.Count == 1 && unexpired[0].Text == "新结果");

        string limitDirectory = Path.Combine(root, "history-limit");
        settings.HistoryRetentionDays = 30;
        var limited = new HistoryStore(limitDirectory);
        for (int i = 0; i < 105; i++) limited.Record(settings, $"原文{i}", $"结果{i}", false);
        var latest = limited.GetAll(settings);
        var reloaded = new HistoryStore(limitDirectory).GetAll(settings);
        Program.Record("历史最多保留最新 100 条", latest.Count == 100 && latest[0].Text == "结果104" && latest[^1].Text == "结果5" && reloaded.Count == 100);

        Guid deleted = latest[10].Id; Guid kept = latest[11].Id;
        limited.Delete(deleted);
        var afterDelete = limited.GetAll(settings);
        Program.Record("历史单条删除不影响其他记录", afterDelete.Count == 99 && afterDelete.All(item => item.Id != deleted) && afterDelete.Any(item => item.Id == kept));
        limited.Clear();
        Program.Record("历史清空删除数据文件", limited.GetAll(settings).Count == 0 && !File.Exists(Path.Combine(limitDirectory, "history.json")));

        string originalOffDirectory = Path.Combine(root, "history-no-original");
        settings.KeepOriginalResult = false;
        var noOriginal = new HistoryStore(originalOffDirectory); noOriginal.Record(settings, "敏感原文", "仅结果", false);
        Program.Record("关闭保留原文时历史不存原文", noOriginal.GetAll(settings).Single().Original == "" && noOriginal.GetAll(settings).Single().Text == "仅结果");

        foreach (var corrupt in new[] { ("broken-json", "not-json"), ("null-entry", "[null]"), ("null-text", "[{\"Id\":\"" + Guid.NewGuid() + "\",\"CreatedAt\":\"2026-11-17T10:00:00\",\"Original\":null,\"Text\":null,\"Submitted\":false}]") })
        {
            string corruptDirectory = Path.Combine(root, corrupt.Item1); Directory.CreateDirectory(corruptDirectory);
            string corruptPath = Path.Combine(corruptDirectory, "history.json"); File.WriteAllText(corruptPath, corrupt.Item2);
            var protectedStore = new HistoryStore(corruptDirectory);
            protectedStore.Record(settings, "不得覆盖", "不得覆盖", true);
            protectedStore.Delete(Guid.NewGuid());
            Program.Record("损坏历史保护 " + corrupt.Item1, protectedStore.LoadWarning != null && protectedStore.GetAll(settings).Count == 0 && File.ReadAllText(corruptPath) == corrupt.Item2);
        }
    }

    private static void TestAppearance(string root)
    {
        string directory = Path.Combine(root, "appearance"); Directory.CreateDirectory(directory);
        string transparent = Path.Combine(directory, "transparent.png");
        SaveImage(transparent, 64, 32, Drawing.Imaging.ImageFormat.Png, graphics =>
        {
            using var brush = new Drawing.SolidBrush(Drawing.Color.Red);
            graphics.FillRectangle(brush, 32, 0, 32, 32);
        }, transparentBackground: true);
        var image = AppearanceService.LoadImage(transparent);
        Program.Record("透明 PNG 解码及尺寸读取", image.PixelWidth == 64 && image.PixelHeight == 32 && Pixel(image, 0, 16).A == 0 && Pixel(image, 48, 16).R > 200);
        var left = AppearanceService.CropRectangle(image, 1, -1, 0);
        var right = AppearanceService.CropRectangle(image, 1, 1, 0);
        Program.Record("裁剪矩形缩放与位置边界", left.Width == 0.5 && left.Height == 1 && left.X == 0 && right.X == 0.5);
        var leftCrop = AppearanceService.Crop(image, 1, -1, 0);
        var rightCrop = AppearanceService.Crop(image, 1, 1, 0);
        Program.Record("裁剪输出保留透明背景", leftCrop.PixelWidth == 256 && leftCrop.PixelHeight == 256 && Pixel(leftCrop, 128, 128).A == 0 && Pixel(rightCrop, 128, 128).R > 200);

        byte[] before = File.ReadAllBytes(transparent);
        string importRoot = Path.Combine(directory, "user-data");
        var assets = AppearanceService.Import(transparent, 1, 1, 0, importRoot);
        bool independent = File.Exists(assets.SourcePath) && File.Exists(assets.OrbPath) && Path.GetFullPath(assets.SourcePath) != Path.GetFullPath(transparent) && before.SequenceEqual(File.ReadAllBytes(transparent));
        File.Delete(transparent);
        Program.Record("导入不修改原图并脱离原路径", independent && !File.Exists(transparent) && AppearanceService.TryLoadImage(assets.SourcePath) != null && AppearanceService.TryLoadImage(assets.OrbPath) != null);

        string jpeg = Path.Combine(directory, "valid.jpg"); SaveImage(jpeg, 24, 24, Drawing.Imaging.ImageFormat.Jpeg);
        Program.Record("JPEG 静态图片支持", AppearanceService.LoadImage(jpeg).PixelWidth == 24);

        string unsupported = Path.Combine(directory, "valid.bmp"); SaveImage(unsupported, 24, 24, Drawing.Imaging.ImageFormat.Bmp);
        string disguisedGif = Path.Combine(directory, "animated.png"); SaveImage(disguisedGif, 24, 24, Drawing.Imaging.ImageFormat.Gif);
        string invalid = Path.Combine(directory, "invalid.png"); File.WriteAllText(invalid, "not an image");
        string empty = Path.Combine(directory, "empty.png"); File.WriteAllBytes(empty, []);
        string oversized = Path.Combine(directory, "oversized.png"); using (var file = File.Create(oversized)) file.SetLength(10 * 1024 * 1024 + 1L);
        string small = Path.Combine(directory, "small.png"); SaveImage(small, 8, 8, Drawing.Imaging.ImageFormat.Png);
        string wide = Path.Combine(directory, "wide.png"); SaveImage(wide, 4097, 16, Drawing.Imaging.ImageFormat.Png);
        string tooManyPixels = Path.Combine(directory, "pixels.png"); SaveImage(tooManyPixels, 4096, 3907, Drawing.Imaging.ImageFormat.Png);
        string[] rejectedFiles = [unsupported, disguisedGif, invalid, empty, oversized, small, wide, tooManyPixels];
        Program.Record("图片格式大小像素及非法文件限制", rejectedFiles.All(path => Rejects(() => AppearanceService.LoadImage(path))));

        string oldAvatar = assets.OrbPath;
        string oldContent = File.ReadAllText(invalid);
        bool oldPreserved = false;
        try { AppearanceService.LoadImage(invalid); }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidDataException or FileFormatException)
        { oldPreserved = AppearanceService.TryLoadImage(oldAvatar) != null && File.ReadAllText(invalid) == oldContent; }
        Program.Record("非法图片不破坏原有形象", oldPreserved && AppearanceService.TryLoadImage(invalid) == null);
    }

    private static bool SendKeys(params (ushort Key, bool Up)[] keys)
    {
        var inputs = keys.Select(item => new NativeMethods.Input
        {
            Type = 1,
            Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { VirtualKey = item.Key, Flags = item.Up ? NativeMethods.KEYEVENTF_KEYUP : 0 } }
        }).ToArray();
        return NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>()) == (uint)inputs.Length;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 150; i++)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException or IOException or FileFormatException) { return true; }
    }

    private static void SaveImage(string path, int width, int height, Drawing.Imaging.ImageFormat format, Action<Drawing.Graphics>? draw = null, bool transparentBackground = false)
    {
        using var bitmap = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(transparentBackground ? Drawing.Color.Transparent : Drawing.Color.White);
            draw?.Invoke(graphics);
        }
        bitmap.Save(path, format);
    }

    private static (byte A, byte R) Pixel(BitmapSource source, int x, int y)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4]; converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[3], pixel[2]);
    }
}
