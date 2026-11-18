using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Suisheng;

public sealed class App : Application
{
    private Mutex? singleton;
    private SessionController? controller;
    private MainWindow? panel;
    private FloatingBall? ball;
    private AiWindow? aiWindow;
    private Forms.NotifyIcon? tray;
    private Icon? trayIcon;
    private bool exiting;

    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleton = new Mutex(true, "Local\\Suisheng.Desktop.0.1", out bool created);
        if (!created)
        {
            NativeMethods.PostMessage(0xFFFF, HotkeyService.ActivateMessage, 0, 0);
            singleton.Dispose(); singleton = null; Shutdown(); return;
        }
        controller = new SessionController(Dispatcher);
        ThemeService.Apply(this, controller.Settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        panel = new MainWindow(controller);
        MainWindow = panel;
        ball = new FloatingBall(controller);
        aiWindow = new AiWindow(controller);
        panel.ExitRequested += () => _ = ExitAsync();
        ball.ExitRequested += () => _ = ExitAsync();
        ball.OpenRequested += () => panel.Open();
        ball.SettingsRequested += () => panel.Open(true);
        ball.AppearanceRequested += () => panel.Open(appearance: true);
        panel.AiOpenRequested += () => aiWindow.Open(false);
        ball.AiOpenRequested += () => aiWindow.Open(false);
        controller.AiPanelRequested += session => aiWindow.Open(session);
        controller.ShowRequested += () => panel.Open();
        controller.SettingsApplied += theme => ThemeService.Apply(this, theme);
        trayIcon = MakeIcon();
        tray = new Forms.NotifyIcon { Text = "随声输入 · 本地语音输入", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主面板", null, (_, _) => panel.Open());
        menu.Items.Add("开始 / 停止录音", null, async (_, _) => await controller.ToggleAsync());
        menu.Items.Add("取消当前任务", null, async (_, _) => await controller.CancelAsync());
        menu.Items.Add("暂停 / 恢复快捷键", null, (_, _) => controller.TogglePause());
        menu.Items.Add("模型与设置", null, (_, _) => panel.Open(true));
        menu.Items.Add("AI 提示词面板", null, (_, _) => aiWindow.Open(false));
        var aiToggle = new Forms.ToolStripMenuItem("开启 AI 优化…");
        aiToggle.Click += async (_, _) => await ToggleAi();
        menu.Items.Add(aiToggle);
        menu.Opened += (_, _) => aiToggle.Text = controller.Settings.AiEnabled ? "关闭 AI 优化" : "开启 AI 优化…";
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出软件", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => panel.Open();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        ball.Show();
        if (!controller.Settings.StartMinimized && Array.IndexOf(e.Args, "--background") < 0) panel.Show();
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _ = controller.CancelAsync();
            MessageBox.Show("操作未完成（" + args.Exception.GetType().Name + "）。当前任务已取消，请检查设备或重新启动。", "随声输入");
        };
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (controller?.Settings.Theme == "system") Dispatcher.BeginInvoke(() => ThemeService.Apply(this, "system"));
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            Dispatcher.BeginInvoke(async () => { if (controller != null) await controller.CancelAsync(); });
    }
    internal static bool RequiresTaskCancellation(PowerModes mode) => mode is PowerModes.Suspend or PowerModes.Resume;
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (RequiresTaskCancellation(e.Mode)) Dispatcher.BeginInvoke(async () => { if (controller != null) await controller.CancelAsync(); });
    }
    private async Task ToggleAi()
    {
        if (controller == null) return;
        try { await controller.SetAiEnabledAsync(!controller.Settings.AiEnabled); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.IO.InvalidDataException or System.IO.IOException or UnauthorizedAccessException)
        { controller.Notify("AI 开关未更改", e.Message); aiWindow?.Open(false); }
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        if (controller != null) await controller.CancelAsync();
        if (panel != null) { panel.AllowClose = true; panel.Close(); }
        if (aiWindow != null) { aiWindow.AllowClose = true; aiWindow.Close(); }
        ball?.Close();
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose(); controller?.Dispose();
        if (singleton != null) { singleton.ReleaseMutex(); singleton.Dispose(); }
        base.OnExit(e);
    }
    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var purple = new SolidBrush(Color.FromArgb(116, 82, 207));
        graphics.FillEllipse(purple, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 3) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        graphics.DrawLine(pen, 11, 13, 11, 19); graphics.DrawLine(pen, 16, 9, 16, 23); graphics.DrawLine(pen, 21, 12, 21, 20);
        nint handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
