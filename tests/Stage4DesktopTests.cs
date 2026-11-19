using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Automation;
using Suisheng;

namespace SuishengTests;

internal static class Stage4DesktopTests
{
    public static async Task RunFirstRun(Process process, string dataDirectory, string root, string screenshotPrefix)
    {
        AutomationElement? guide = await FindProcessWindow(process, "随声输入 · 首次引导", 8000);
        bool found = guide != null;
        bool allSteps = found;
        if (guide != null)
        {
            Capture(guide, Path.Combine(root, screenshotPrefix + "-onboarding.png"));
            string[] headings = ["先确认麦克风", "准备本地识别模型", "确认全局快捷键", "认识常驻悬浮球", "做一次安全输入测试", "可选 AI 与数据发送"];
            foreach (string heading in headings)
            {
                allSteps &= HasText(guide, heading);
                Click(Find(guide, "NextButton"));
                await Task.Delay(180);
            }
            await WaitUntil(() => !IsWindow((nint)guide.Current.NativeWindowHandle), 4000);
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dataDirectory, "settings.json")));
        bool saved = document.RootElement.GetProperty("OnboardingCompleted").GetBoolean();
        Program.Record("首次启动六项引导完整展示并持久化", found && allSteps && saved, $"找到={found}；六页={allSteps}；保存={saved}");
    }

    public static async Task RunHelp(AutomationElement panel, Process process, string dataDirectory, string root)
    {
        var help = panel.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem), new PropertyCondition(AutomationElement.NameProperty, "阶段与帮助")));
        ((SelectionItemPattern)help.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await Task.Delay(250);
        bool updateText = HasText(panel, "当前没有在线更新服务") && HasText(panel, "手动更新");
        Click(Find(panel, "OpenOnboardingButton"));
        AutomationElement? guide = await FindProcessWindow(process, "随声输入 · 首次引导", 4000);
        if (guide != null)
        {
            Click(Find(guide, "SkipButton"));
            await Task.Delay(200);
        }
        Program.Record("帮助页手动更新说明及重新打开首次引导", updateText && guide != null);

        string exportPath = Path.Combine(root, "stage4-diagnostics-ui.json");
        if (File.Exists(exportPath)) File.Delete(exportPath);
        panel.SetFocus();
        Click(Find(panel, "ExportDiagnosticsButton"));
        nint dialog = 0;
        await WaitUntil(() => (dialog = FindWindow(null, "导出不含正文和密钥的诊断信息")) != 0, 5000);
        bool dialogOpened = dialog != 0;
        bool dialogClosed = false;
        nint save = 0;
        AutomationElement? edit = null;
        if (dialogOpened)
        {
            edit = AutomationElement.FromHandle(dialog).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            save = GetDlgItem(dialog, 1);
            SetForegroundWindow(dialog);
            System.Windows.Forms.SendKeys.SendWait("%n");
            await Task.Delay(150);
            System.Windows.Forms.SendKeys.SendWait("^a");
            System.Windows.Forms.SendKeys.SendWait(exportPath);
            await Task.Delay(250);
            if (save != 0)
            {
                GetWindowRect(save, out NativeRect saveRect);
                SetCursorPos((saveRect.Left + saveRect.Right) / 2, (saveRect.Top + saveRect.Bottom) / 2);
                mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
            }
            dialogClosed = await WaitUntil(() => !IsWindow(dialog), 5000);
        }
        AutomationElement? completed = await FindProcessWindow(process, "导出完成", 4000);
        if (completed != null) Click(FindByName(completed, "确定"));
        bool valid = File.Exists(exportPath);
        if (valid)
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
            string json = await File.ReadAllTextAsync(exportPath);
            valid = document.RootElement.GetProperty("schema").GetInt32() == 1 &&
                !json.Contains(dataDirectory, StringComparison.OrdinalIgnoreCase) &&
                !json.Contains("AiEndpoint", StringComparison.Ordinal) &&
                !json.Contains("ai-credential", StringComparison.Ordinal);
        }
        Program.Record("界面通过真实保存对话框导出安全诊断", dialogOpened && dialogClosed && valid, $"对话框={dialogOpened}；关闭={dialogClosed}；编辑框={edit != null}；保存按钮={save != 0}；完成提示={completed != null}；文件有效={valid}");
    }

    private static AutomationElement Find(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ??
        throw new InvalidOperationException("找不到控件 " + id);

    private static AutomationElement FindByName(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name))) ??
        throw new InvalidOperationException("找不到按钮 " + name);

    private static void Click(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static bool HasText(AutomationElement root, string text) => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
        .Cast<AutomationElement>().Any(item => item.Current.Name.Contains(text, StringComparison.Ordinal));

    private static async Task<AutomationElement?> FindProcessWindow(Process process, string title, int timeout)
    {
        AutomationElement? result = null;
        await WaitUntil(() =>
        {
            result = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                new PropertyCondition(AutomationElement.NameProperty, title)));
            return result != null;
        }, timeout);
        return result;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static void Capture(AutomationElement element, string path)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var image = new System.Drawing.Bitmap((int)rect.Width, (int)rect.Height);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, image.Size);
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
