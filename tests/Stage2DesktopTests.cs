using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Suisheng;
using Drawing = System.Drawing;
using Clipboard = System.Windows.Clipboard;

namespace SuishengTests;

internal static class Stage2DesktopTests
{
    public static async Task Run(AutomationElement panel, AutomationElement ball, Process process, string root, string dataDirectory, string screenshotPrefix)
    {
        AutomationElement Find(string id)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var element = panel.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                if (element != null) return element;
                System.Threading.Thread.Sleep(60);
            }
            return Require(null, id);
        }
        var tabs = new (string Name, string Anchor)[] { ("语音输入", "StatusText"), ("模型与录音", "ModelDirectoryBox"), ("文字处理", "ReplacementRulesBox"), ("悬浮球外观", "AvatarStatus"), ("历史与隐私", "HistoryList") };
        bool tabsOpened = true;
        foreach (var tab in tabs)
        {
            await SelectTab(panel, tab.Name);
            tabsOpened &= Find(tab.Anchor) != null;
        }
        await SelectTab(panel, "阶段与帮助");
        bool versionVisible = panel.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Any(element => element.Current.Name.Contains("当前版本 0.4.0"));
        Program.Record("第二阶段全部新页签可打开", tabsOpened && versionVisible, $"页签={tabsOpened}；版本文本={versionVisible}");

        await SelectTab(panel, "模型与录音");
        var hotkeyBox = Find("HotkeyBox");
        hotkeyBox.SetFocus(); await Task.Delay(200);
        PhysicalClick(hotkeyBox);
        SendCombo(0x7A);
        await Task.Delay(250);
        string capturedHotkey = ((ValuePattern)hotkeyBox.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        Click(Find("SaveButton")); await Task.Delay(350);
        var changedHotkey = ReadSettings(dataDirectory);
        bool replacementRegistered = false;
        if (changedHotkey.HotkeyKey == 0x7A)
        {
            using var probe = new HotkeyService();
            replacementRegistered = !probe.TrySet(3, 0x7A);
        }
        bool hotkeyCaptureSaved = capturedHotkey.Contains("F11") && changedHotkey.HotkeyModifiers == 3 && changedHotkey.HotkeyKey == 0x7A && replacementRegistered;
        if (!hotkeyCaptureSaved) Capture(panel, Path.Combine(root, screenshotPrefix + "-hotkey-failure.png"));
        Program.Record("界面录制修改快捷键并立即注册", hotkeyCaptureSaved, $"输入框={capturedHotkey}；保存修饰键={changedHotkey.HotkeyModifiers}；保存按键={changedHotkey.HotkeyKey}；系统注册={replacementRegistered}");
        if (changedHotkey.HotkeyKey == 0x7A)
        {
            hotkeyBox.SetFocus(); await Task.Delay(150); PhysicalClick(hotkeyBox); SendCombo(0x20); await Task.Delay(200);
        }
        else Click(FindByName(panel, "恢复默认快捷键", ControlType.Button));
        Click(Find("SaveButton")); await Task.Delay(300);
        var restoredHotkey = ReadSettings(dataDirectory);
        Program.Record("界面恢复默认快捷键并保存", restoredHotkey.HotkeyModifiers == 3 && restoredHotkey.HotkeyKey == 0x20);

        await SelectTab(panel, "悬浮球外观");
        panel.SetFocus(); await Task.Delay(150);
        double light = Capture(panel, Path.Combine(root, screenshotPrefix + "-light.png"));
        await SelectComboItem(Find("ThemeCombo"), "深色");
        Click(Find("SaveButton")); await Task.Delay(400);
        var darkSettings = ReadSettings(dataDirectory);
        panel.SetFocus(); await Task.Delay(150);
        double dark = Capture(panel, Path.Combine(root, screenshotPrefix + "-dark.png"));
        Program.Record("深色主题实际应用并保存", darkSettings.Theme == "dark" && dark < light * 0.8, $"浅色亮度 {light:F1}，深色亮度 {dark:F1}");

        var themeCombo = Find("ThemeCombo");
        var expand = (ExpandCollapsePattern)themeCombo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand(); await Task.Delay(200);
        bool comboItems = new[] { "浅色", "深色", "跟随 Windows 系统" }.All(name => themeCombo.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, name))) != null);
        Capture(panel, Path.Combine(root, screenshotPrefix + "-theme-dropdown.png"));
        expand.Collapse();
        Program.Record("深色主题 ComboBox 选项可见", comboItems);

        await SelectComboItem(themeCombo, "跟随 Windows 系统"); Click(Find("SaveButton")); await Task.Delay(250);
        bool systemSaved = ReadSettings(dataDirectory).Theme == "system";
        await SelectComboItem(themeCombo, "浅色"); Click(Find("SaveButton")); await Task.Delay(250);
        Program.Record("跟随系统及浅色主题保存恢复", systemSaved && ReadSettings(dataDirectory).Theme == "light");

        string avatarSource = Path.Combine(root, screenshotPrefix + "-avatar-source.png");
        SaveAvatar(avatarSource);
        await ChooseFile(process, panel, FindByName(panel, "选择 PNG / JPEG 图片…", ControlType.Button), avatarSource);
        await Task.Delay(250);
        string pendingStatus = Find("AvatarStatus").Current.Name;
        Capture(panel, Path.Combine(root, screenshotPrefix + "-avatar-preview.png"));
        Program.Record("图片选择后显示真实预览", pendingStatus.Contains("正在预览新图片"));

        string invalid = Path.Combine(root, screenshotPrefix + "-invalid.png"); File.WriteAllText(invalid, "not an image");
        await ChooseFile(process, panel, FindByName(panel, "选择 PNG / JPEG 图片…", ControlType.Button), invalid);
        AutomationElement? warning = await FindDialog(process, "图片未导入", 3000);
        if (warning != null) Click(FindByName(warning, "确定", ControlType.Button));
        Program.Record("非法图片提示且保留当前预览", warning != null && Find("AvatarStatus").Current.Name == pendingStatus);

        var avatarStatusRect = Find("AvatarStatus").Current.BoundingRectangle;
        var previewRect = new System.Windows.Rect(avatarStatusRect.X - 244, avatarStatusRect.Y - 50, 220, 220);
        Drag(previewRect, 32, 18); await Task.Delay(200);
        ((RangeValuePattern)Find("AvatarZoomSlider").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(2);
        ((RangeValuePattern)Find("BallSizeSlider").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(110);
        ((RangeValuePattern)Find("BallOpacitySlider").GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(0.8);
        Click(Find("SaveButton")); await Task.Delay(500);
        var avatarSettings = ReadSettings(dataDirectory);
        string importedOrb = avatarSettings.OrbImagePath;
        string importedSource = avatarSettings.AvatarSourcePath;
        bool savedPreviewStatus = Find("AvatarStatus").Current.Name.Contains("已保存的形象");
        Program.Record("图片拖动裁剪大小透明度及导入保存", avatarSettings.AvatarOffsetX != 0 && avatarSettings.AvatarZoom == 2 && avatarSettings.BallSize == 110 && Math.Abs(avatarSettings.BallOpacity - 0.8) < 0.001 && File.Exists(importedOrb) && File.Exists(importedSource) && Path.GetFullPath(importedOrb).StartsWith(Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase) && savedPreviewStatus);
        File.Delete(avatarSource);
        Capture(ball, Path.Combine(root, screenshotPrefix + "-custom-ball.png"));
        Program.Record("导入图片脱离原路径且悬浮球立即使用", !File.Exists(avatarSource) && AppearanceService.TryLoadImage(importedOrb) != null && HasText(ball, "待机"));

        await SelectTab(panel, "历史与隐私");
        bool historyDefaultOff = ((TogglePattern)Find("HistoryEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off;
        var historyItems = Find("HistoryList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        Program.Record("历史界面默认关闭且可查看已有记录", historyDefaultOff && historyItems.Count == 2);
        if (historyItems.Count > 0)
        {
            ((SelectionItemPattern)historyItems[0].GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); await Task.Delay(150);
            bool hadText = Clipboard.ContainsText(); string? previous = hadText ? Clipboard.GetText() : null;
            try
            {
                Click(Find("HistoryCopyButton")); await Task.Delay(150);
                string detail = ((ValuePattern)Find("HistoryDetailBox").GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                Program.Record("历史选中详情及复制操作", detail.Contains("桌面历史") && Clipboard.GetText().Contains("桌面历史"));
            }
            finally
            {
                if (hadText) Clipboard.SetText(previous!); else Clipboard.Clear();
            }
            Click(Find("HistoryDeleteButton"));
            var confirmation = await FindDialog(process, "删除历史", 3000);
            if (confirmation != null) Click(FindByNameContains(confirmation, "是", ControlType.Button));
            await Task.Delay(300);
            Program.Record("历史单条删除界面操作", confirmation != null && Find("HistoryList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count == 1);
        }
        Click(Find("HistoryClearButton"));
        var clearConfirmation = await FindDialog(process, "清空历史", 3000);
        if (clearConfirmation != null) Click(FindByNameContains(clearConfirmation, "是", ControlType.Button));
        await Task.Delay(300);
        Program.Record("历史清空界面操作", clearConfirmation != null && Find("HistoryList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count == 0 && !File.Exists(Path.Combine(dataDirectory, "history.json")));

        var historyToggle = (TogglePattern)Find("HistoryEnabledBox").GetCurrentPattern(TogglePattern.Pattern);
        historyToggle.Toggle(); Click(Find("SaveButton")); await Task.Delay(300);
        bool enabledSaved = ReadSettings(dataDirectory).HistoryEnabled;
        historyToggle.Toggle(); Click(Find("SaveButton")); await Task.Delay(300);
        Program.Record("历史开关保存且不默认开启", enabledSaved && !ReadSettings(dataDirectory).HistoryEnabled);

        await SelectTab(panel, "语音输入");
        await InvokeBallMenu(ball, process, "暂停快捷键"); await Task.Delay(250);
        bool paused = Find("HotkeyText").Current.Name.Contains("已暂停") && HasText(ball, "暂停");
        await InvokeBallMenu(ball, process, "恢复快捷键"); await Task.Delay(250);
        Program.Record("悬浮球暂停与恢复状态同步", paused && HasText(ball, "待机"));

        await SelectTab(panel, "模型与录音");
        string missingModels = Path.Combine(dataDirectory, "missing-models");
        ((ValuePattern)Find("ModelDirectoryBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue(missingModels);
        Click(Find("SaveButton")); await Task.Delay(300);
        await SelectTab(panel, "语音输入");
        Click(Find("RecordButton"));
        bool failed = await WaitUntil(() => Find("StatusText").Current.Name == "无法开始", 8000);
        await Task.Delay(200);
        Program.Record("模型缺失不启动录音且悬浮球显示错误", failed && HasText(ball, "错误") && Find("RecordButton").Current.IsEnabled);
        Capture(ball, Path.Combine(root, screenshotPrefix + "-error-ball.png"));
    }

    private static async Task SelectTab(AutomationElement panel, string name)
    {
        var tab = Require(panel.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem), new PropertyCondition(AutomationElement.NameProperty, name))), name);
        ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await Task.Delay(180);
    }

    private static async Task SelectComboItem(AutomationElement combo, string name)
    {
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand(); await Task.Delay(120);
        var item = Require(combo.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, name))), name);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse(); await Task.Delay(120);
    }

    private static async Task ChooseFile(Process process, AutomationElement panel, AutomationElement button, string path)
    {
        panel.SetFocus();
        var buttonRect = button.Current.BoundingRectangle;
        SetCursorPos((int)(buttonRect.X + buttonRect.Width / 2), (int)(buttonRect.Y + buttonRect.Height / 2));
        mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
        nint dialog = 0;
        await WaitUntil(() => (dialog = FindWindow(null, "选择悬浮球图片（不超过 10 MB）")) != 0, 5000);
        if (dialog == 0) throw new InvalidOperationException("图片文件对话框未打开");
        nint comboEx = FindWindowEx(dialog, 0, "ComboBoxEx32", null);
        nint combo = FindWindowEx(comboEx, 0, "ComboBox", null);
        nint edit = FindWindowEx(combo, 0, "Edit", null);
        if (edit == 0) throw new InvalidOperationException("找不到图片文件名编辑框");
        SetForegroundWindow(dialog);
        GetWindowRect(edit, out NativeRect editRect);
        SetCursorPos((editRect.Left + editRect.Right) / 2, (editRect.Top + editRect.Bottom) / 2);
        mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
        await Task.Delay(200);
        TypeText(path);
        await Task.Delay(300);
        nint open = GetDlgItem(dialog, 1);
        if (open == 0) throw new InvalidOperationException("找不到图片文件对话框打开按钮");
        GetWindowRect(open, out NativeRect openRect);
        SetCursorPos((openRect.Left + openRect.Right) / 2, (openRect.Top + openRect.Bottom) / 2);
        mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
        bool closed = await WaitUntil(() => !IsWindow(dialog), 5000);
        if (!closed)
        {
            var fileName = new StringBuilder(4096);
            GetWindowText(edit, fileName, fileName.Capacity);
            Capture(AutomationElement.FromHandle(dialog), Path.Combine(Path.GetDirectoryName(path)!, "dialog-open-failed.png"));
            throw new InvalidOperationException($"图片文件对话框未接受所选文件；文件存在={File.Exists(path)}，文件名框={fileName}");
        }
    }

    private static async Task<AutomationElement?> FindDialog(Process process, string title, int timeout)
    {
        string exactTitle = title == "图片未导入" ? "图片未导入，原形象已保留" : title;
        AutomationElement? result = null;
        await WaitUntil(() =>
        {
            nint handle = FindWindow(null, exactTitle);
            if (handle == 0) return false;
            var candidate = AutomationElement.FromHandle(handle);
            if (candidate.Current.ProcessId != process.Id) return false;
            result = candidate;
            return true;
        }, timeout);
        return result;
    }

    private static async Task InvokeBallMenu(AutomationElement ball, Process process, string itemName)
    {
        var rect = ball.Current.BoundingRectangle;
        SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        mouse_event(0x0008, 0, 0, 0, 0); mouse_event(0x0010, 0, 0, 0, 0);
        await Task.Delay(200);
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        foreach (AutomationElement window in windows)
        {
            var item = window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem), new PropertyCondition(AutomationElement.NameProperty, itemName)));
            if (item != null) { ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return; }
        }
        throw new InvalidOperationException("找不到悬浮球菜单项 " + itemName);
    }

    private static UserSettings ReadSettings(string dataDirectory) => JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path.Combine(dataDirectory, "settings.json"))) ?? throw new InvalidDataException("设置文件为空");

    private static bool HasText(AutomationElement element, string text) => element.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text), new PropertyCondition(AutomationElement.NameProperty, text))) != null;

    private static AutomationElement FindByName(AutomationElement root, string name, ControlType type) => Require(root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, type), new PropertyCondition(AutomationElement.NameProperty, name))), name);

    private static AutomationElement FindByNameContains(AutomationElement root, string name, ControlType type)
    {
        var elements = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return Require(elements.Cast<AutomationElement>().FirstOrDefault(element => element.Current.Name.Contains(name)), name);
    }

    private static AutomationElement Require(AutomationElement? element, string name) => element ?? throw new InvalidOperationException("找不到界面元素 " + name);
    private static void Click(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static double Capture(AutomationElement element, string path)
    {
        var rect = element.Current.BoundingRectangle;
        int width = Math.Max(1, (int)rect.Width), height = Math.Max(1, (int)rect.Height);
        using var bitmap = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, bitmap.Size);
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
        double sum = 0; int count = 0;
        for (int y = 0; y < height; y += 8)
        for (int x = 0; x < width; x += 8)
        {
            var color = bitmap.GetPixel(x, y); sum += 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B; count++;
        }
        return sum / count;
    }

    private static void SaveAvatar(string path)
    {
        using var bitmap = new Drawing.Bitmap(80, 40);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Drawing.Color.Transparent);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 80, 70, 160));
            graphics.FillEllipse(brush, 40, 0, 40, 40);
        }
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
    }

    private static void Drag(System.Windows.Rect rect, int deltaX, int deltaY)
    {
        int x = (int)(rect.X + rect.Width / 2), y = (int)(rect.Y + rect.Height / 2);
        SetCursorPos(x, y); mouse_event(0x0002, 0, 0, 0, 0);
        for (int i = 1; i <= 6; i++) { SetCursorPos(x + deltaX * i / 6, y + deltaY * i / 6); System.Threading.Thread.Sleep(25); }
        mouse_event(0x0004, 0, 0, 0, 0);
    }

    private static void PhysicalClick(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
    }

    private static void SendCombo(ushort key)
    {
        var keys = new[] { (Key: (ushort)0x11, Up: false), (Key: (ushort)0x12, Up: false), (Key: key, Up: false), (Key: key, Up: true), (Key: (ushort)0x12, Up: true), (Key: (ushort)0x11, Up: true) };
        var inputs = keys.Select(item => new NativeMethods.Input { Type = 1, Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { VirtualKey = item.Key, Flags = item.Up ? NativeMethods.KEYEVENTF_KEYUP : 0 } } }).ToArray();
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != (uint)inputs.Length)
            throw new InvalidOperationException("无法发送快捷键测试按键");
    }

    private static void TypeText(string text)
    {
        var inputs = new NativeMethods.Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new NativeMethods.Input { Type = 1, Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { Scan = text[i], Flags = NativeMethods.KEYEVENTF_UNICODE } } };
            inputs[i * 2 + 1] = new NativeMethods.Input { Type = 1, Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { Scan = text[i], Flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } } };
        }
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != (uint)inputs.Length)
            throw new InvalidOperationException("无法通过键盘输入图片路径");
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int itemId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
