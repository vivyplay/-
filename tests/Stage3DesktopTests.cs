using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;
using Suisheng;

namespace SuishengTests;

internal static class Stage3DesktopTests
{
    public static async Task Run(AutomationElement panel, AutomationElement ball, Process process, string root, string dataDirectory, string screenshotPrefix)
    {
        AutomationElement Find(AutomationElement parent, string id)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var element = parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                if (element != null) return element;
                Thread.Sleep(60);
            }
            return Require(null, id);
        }
        SelectTab(panel, "语音输入");
        bool defaultOff = ((TogglePattern)Find(panel, "AiEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off &&
            !JsonSettings(dataDirectory).AiEnabled;
        Program.Record("主界面 AI 默认关闭", defaultOff);
        Click(Find(panel, "OpenAiButton"));
        var ai = await FindWindow(process, "随声输入 · AI 提示词优化", 5000);
        Program.Record("AI 主面板实际打开", ai != null);
        if (ai == null) return;
        PhysicalClick(Find(ai, "AiEnabledBox"));
        var incomplete = await FindDialog(process, "AI 开关未更改", 3000);
        if (incomplete != null) ClickName(incomplete, "确定");
        var incompleteState = ((TogglePattern)Find(ai, "AiEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;
        string topWindows = string.Join(" | ", AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)).Cast<AutomationElement>().Select(item => item.Current.Name + "/" + item.Current.ControlType.ProgrammaticName));
        Program.Record("配置不完整时开启 AI 有明确提示且不改变状态", incomplete != null && incompleteState == ToggleState.Off, $"提示={incomplete != null}；开关={incompleteState}；窗口={topWindows}");

        using var server = new ContractServer();
        SelectTab(ai, "API 配置");
        SetValue(Find(ai, "EndpointBox"), server.Endpoint + "/v1/chat/completions");
        SetValue(Find(ai, "ModelBox"), "desktop-contract-model");
        SetValue(Find(ai, "TimeoutBox"), "20");
        Find(ai, "ApiKeyBox").SetFocus(); SendKeys.SendWait("desktop-contract-key"); await Task.Delay(150);
        Click(Find(ai, "SaveConfigButton")); await Task.Delay(300);
        var saved = JsonSettings(dataDirectory);
        string credentialPath = Path.Combine(dataDirectory, "ai-credential.bin");
        string settingsText = File.ReadAllText(Path.Combine(dataDirectory, "settings.json"));
        Program.Record("真实界面保存 AI 配置及 DPAPI 密钥", saved.AiEndpoint.EndsWith("/v1/chat/completions", StringComparison.Ordinal) && saved.AiModel == "desktop-contract-model" && File.Exists(credentialPath) &&
            !settingsText.Contains("desktop-contract-key", StringComparison.Ordinal) && !Convert.ToBase64String(File.ReadAllBytes(credentialPath)).Contains("desktop-contract-key", StringComparison.Ordinal));
        Capture(ai, Path.Combine(root, screenshotPrefix + "-ai-api.png"));
        PhysicalClick(Find(ai, "TestConnectionButton"));
        var testConfirmation = await FindDialog(process, "真实 AI 连接测试", 3000);
        if (testConfirmation != null) ClickName(testConfirmation, "确定");
        bool connected = await WaitUntil(() => Find(ai, "GlobalStatus").Current.Name.Contains("AI 连接成功", StringComparison.Ordinal), 5000);
        Program.Record("界面发起真实回环 Chat Completions 连接测试", testConfirmation != null && connected && server.Requests == 1, $"确认={testConfirmation != null}；成功={connected}；请求={server.Requests}；状态={Find(ai, "GlobalStatus").Current.Name}");
        Toggle(Find(ai, "AiEnabledBox")); await Task.Delay(250);
        bool mainOn = ((TogglePattern)Find(panel, "AiEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
        SelectTab(panel, "模型与录音");
        bool settingsOn = ((TogglePattern)Find(panel, "AiSettingsEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
        SelectTab(panel, "语音输入");
        Program.Record("AI 面板主面板与设置开关同步", JsonSettings(dataDirectory).AiEnabled && mainOn && settingsOn);

        SelectTab(ai, "模板管理");
        int builtIns = Find(ai, "TemplateList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
        ClickName(ai, "新建");
        SetValue(Find(ai, "TemplateNameBox"), "桌面契约模板");
        SetValue(Find(ai, "TemplateDescriptionBox"), "真实界面模板测试");
        ClickName(ai, "保存当前模板"); await Task.Delay(250);
        int customCount = Find(ai, "TemplateList").FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
        bool customSaved = customCount == 6 && new AiTemplateStore(dataDirectory).GetAll().Any(item => item.Name == "桌面契约模板");
        Capture(ai, Path.Combine(root, screenshotPrefix + "-ai-templates.png"));
        PhysicalClick(Require(ai.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "删除"))), "删除"));
        var delete = await FindDialog(process, "删除模板", 3000);
        if (delete != null) ClickNameContains(delete, "是");
        await Task.Delay(250);
        Program.Record("模板界面新建保存及删除", builtIns == 5 && customSaved && delete != null && new AiTemplateStore(dataDirectory).GetAll().Count == 5);

        SelectTab(ai, "优化预览");
        SetValue(Find(ai, "OriginalBox"), "桌面手动原文");
        SetValue(Find(ai, "InstructionsBox"), "保留原意并理顺表达");
        PhysicalClick(Find(ai, "OptimizeButton"));
        var optimizeConfirmation = await FindDialog(process, "确认发送 AI 优化请求", 3000);
        if (optimizeConfirmation != null) ClickName(optimizeConfirmation, "确定");
        bool preview = await WaitUntil(() => Find(ai, "SessionStatus").Current.Name.Contains("AI 结果待确认", StringComparison.Ordinal), 5000);
        string optimized = Value(Find(ai, "ResultBox"));
        Program.Record("真实界面请求后进入预览而非直接写入", optimizeConfirmation != null && preview && optimized == "AI 桌面优化结果" && server.Requests == 2);
        Capture(ai, Path.Combine(root, screenshotPrefix + "-ai-preview.png"));
        SetValue(Find(ai, "ResultBox"), "人工编辑后的桌面结果");
        PhysicalClick(Find(ai, "ConfirmWriteButton"));
        var writeConfirmation = await FindDialog(process, "确认 AI 结果并重新选择目标", 3000);
        if (writeConfirmation != null) ClickName(writeConfirmation, "确定");
        bool writeFinished = await WaitUntil(() => Find(panel, "StatusText").Current.Name == "待手动输入", 10000);
        string writeResult = Value(Find(panel, "ResultBox"));
        Program.Record("确认编辑结果后重新验证目标且不向自身窗口误写", writeConfirmation != null && writeFinished && writeResult == "人工编辑后的桌面结果",
            $"确认={writeConfirmation != null}；完成={writeFinished}；状态={Find(panel, "StatusText").Current.Name}；结果={writeResult}");

        Click(Find(panel, "OpenAiButton"));
        ai = await FindWindow(process, "随声输入 · AI 提示词优化", 3000) ?? ai;
        SelectTab(ai, "优化预览"); SetValue(Find(ai, "OriginalBox"), "关闭时取消原文");
        PhysicalClick(Find(ai, "OptimizeButton"));
        var lateConfirmation = await FindDialog(process, "确认发送 AI 优化请求", 3000);
        if (lateConfirmation != null) ClickName(lateConfirmation, "确定");
        await WaitUntil(() => server.Requests >= 3, 3000);
        ai.SetFocus(); SendKeys.SendWait("%{F4}"); await Task.Delay(1700);
        string lateResult = Value(Find(panel, "ResultBox")); string lateStatus = Find(panel, "StatusText").Current.Name;
        Program.Record("关闭优化窗口后迟到结果不进入预览或写入", lateResult == "关闭时取消原文" && lateStatus.Contains("已取消", StringComparison.Ordinal), $"状态={lateStatus}；结果={lateResult}；请求={server.Requests}");

        Toggle(Find(panel, "AiEnabledBox")); await Task.Delay(250);
        SelectTab(panel, "模型与录音");
        bool settingsOff = ((TogglePattern)Find(panel, "AiSettingsEnabledBox").GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off;
        SelectTab(panel, "语音输入");
        bool mainAndSettingsOff = !JsonSettings(dataDirectory).AiEnabled && settingsOff;
        var rect = ball.Current.BoundingRectangle;
        MouseRightClick((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2)); await Task.Delay(250);
        AutomationElement? aiMenu = null;
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)))
        {
            aiMenu = window.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem), new PropertyCondition(AutomationElement.NameProperty, "开启 AI 优化…")));
            if (aiMenu != null) break;
        }
        Program.Record("主面板设置及悬浮球 AI 状态同步关闭", mainAndSettingsOff && aiMenu != null);
        SendKeys.SendWait("{ESC}");
        Click(Find(panel, "OpenAiButton")); await Task.Delay(250);
        int requests = server.Requests;
        Program.Record("AI 关闭后界面禁止发送优化请求", !Find(ai, "OptimizeButton").Current.IsEnabled && server.Requests == requests);
        ((WindowPattern)ai.GetCurrentPattern(WindowPattern.Pattern)).Close();
        SelectTab(panel, "悬浮球外观"); SelectComboItem(Find(panel, "ThemeCombo"), "深色"); Click(Find(panel, "SaveButton")); await Task.Delay(350);
        SelectTab(panel, "语音输入"); Click(Find(panel, "OpenAiButton")); await Task.Delay(300);
        double darkBrightness = Capture(ai, Path.Combine(root, screenshotPrefix + "-ai-dark.png"));
        Program.Record("AI 面板深色主题实际应用", darkBrightness < 100, $"深色亮度={darkBrightness:F1}");
        ((WindowPattern)ai.GetCurrentPattern(WindowPattern.Pattern)).Close();
        SelectTab(panel, "悬浮球外观"); SelectComboItem(Find(panel, "ThemeCombo"), "浅色"); Click(Find(panel, "SaveButton")); await Task.Delay(250);
        SelectTab(panel, "语音输入");
    }

    private static UserSettings JsonSettings(string root) => new SettingsStore(root).Load();
    private static string Value(AutomationElement element) => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static void SetValue(AutomationElement element, string value) => ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static void Toggle(AutomationElement element) => ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    private static void Click(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    private static void ClickName(AutomationElement parent, string name) => Click(Require(parent.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name))), name));
    private static void ClickNameContains(AutomationElement parent, string name)
    {
        var item = parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>().FirstOrDefault(element => element.Current.Name.Contains(name, StringComparison.Ordinal));
        if (item != null) Click(item);
    }
    private static void SelectComboItem(AutomationElement combo, string name)
    {
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand(); Thread.Sleep(150);
        var item = Require(combo.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, name))), name);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); expand.Collapse(); Thread.Sleep(150);
    }
    private static void SelectTab(AutomationElement parent, string name)
    {
        var tab = Require(parent.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem), new PropertyCondition(AutomationElement.NameProperty, name))), name);
        ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); Thread.Sleep(180);
    }
    private static async Task<AutomationElement?> FindWindow(Process process, string title, int timeout)
    {
        AutomationElement? found = null;
        await WaitUntil(() => (found = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.NameProperty, title)))) != null, timeout);
        return found;
    }
    private static async Task<AutomationElement?> FindDialog(Process process, string title, int timeout)
    {
        AutomationElement? result = null;
        await WaitUntil(() =>
        {
            nint handle = FindWindow(null, title);
            if (handle == 0) return false;
            var candidate = AutomationElement.FromHandle(handle);
            if (candidate.Current.ProcessId != process.Id) return false;
            result = candidate; return true;
        }, timeout);
        return result;
    }
    private static async Task<bool> WaitUntil(Func<bool> condition, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeout) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }
    private static AutomationElement Require(AutomationElement? element, string name) => element ?? throw new InvalidOperationException("找不到界面元素 " + name);
    private static double Capture(AutomationElement element, string path)
    {
        var rect = element.Current.BoundingRectangle;
        int width = Math.Max(1, (int)rect.Width), height = Math.Max(1, (int)rect.Height);
        using var image = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(image); graphics.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, image.Size);
        image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        double sum = 0; int count = 0;
        for (int y = 0; y < height; y += 8)
        for (int x = 0; x < width; x += 8)
        {
            var color = image.GetPixel(x, y); sum += 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B; count++;
        }
        return sum / count;
    }
    private static void PhysicalClick(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        mouse_event(0x0002, 0, 0, 0, 0); mouse_event(0x0004, 0, 0, 0, 0);
    }
    private static void MouseRightClick(int x, int y)
    {
        SetCursorPos(x, y); mouse_event(0x0008, 0, 0, 0, 0); mouse_event(0x0010, 0, 0, 0, 0);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern nint FindWindow(string? className, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    private sealed class ContractServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task serving;
        private int requests;
        public string Endpoint { get; }
        public int Requests => Volatile.Read(ref requests);

        public ContractServer()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); int port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
            Endpoint = $"http://127.0.0.1:{port}"; listener.Prefixes.Add(Endpoint + "/"); listener.Start(); serving = Serve();
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }
                _ = Task.Run(async () =>
                {
                    Interlocked.Increment(ref requests);
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8); string body = await reader.ReadToEndAsync();
                        using var document = JsonDocument.Parse(body);
                        string user = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
                        bool connection = user.Contains("只回复：连接成功", StringComparison.Ordinal);
                        bool delayed = user.Contains("关闭时取消原文", StringComparison.Ordinal);
                        if (delayed) await Task.Delay(1000);
                        string result = connection ? "连接成功" : delayed ? "迟到的 AI 结果" : "AI 桌面优化结果";
                        string json = "{\"choices\":[{\"message\":{\"content\":\"" + result + "\"}}]}";
                        byte[] bytes = Encoding.UTF8.GetBytes(json); context.Response.StatusCode = 200; context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
                });
            }
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}
