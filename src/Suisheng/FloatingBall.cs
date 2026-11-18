using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Suisheng;

public sealed class FloatingBall : Window
{
    private readonly SessionController controller;
    private readonly Border orb;
    private readonly TextBlock label, stateLabel;
    private readonly System.Windows.Shapes.Ellipse avatar;
    private string? loadedImagePath;
    private Point origin;
    private Point windowOrigin;
    private bool pressed, dragged;
    private readonly MenuItem recordItem, cancelItem, pauseItem;
    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? AppearanceRequested;
    public event Action? ExitRequested;

    public FloatingBall(SessionController controller)
    {
        this.controller = controller;
        Width = controller.Settings.BallSize; Height = controller.Settings.BallSize + 18;
        Opacity = controller.Settings.BallOpacity;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "随声输入 · 悬浮球";
        label = new TextBlock { Text = "声", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        avatar = new System.Windows.Shapes.Ellipse { Visibility = Visibility.Collapsed };
        var content = new Grid();
        content.Children.Add(avatar); content.Children.Add(label);
        orb = new Border { Margin = new Thickness(5), BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Color.FromRgb(196, 181, 253)), Background = new SolidColorBrush(Color.FromRgb(105, 75, 210)), Child = content, Cursor = Cursors.Hand };
        orb.SizeChanged += (_, e) => orb.CornerRadius = new CornerRadius(e.NewSize.Width / 2);
        stateLabel = new TextBlock { Text = "待机", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(40, 36, 60)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 2, 0, 0) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(orb);
        Grid.SetRow(stateLabel, 1); layout.Children.Add(stateLabel);
        Content = layout;
        var menu = new ContextMenu();
        Add(menu, "打开软件面板", () => OpenRequested?.Invoke());
        recordItem = Add(menu, "开始录音", () => _ = controller.ToggleAsync());
        cancelItem = Add(menu, "取消当前任务", () => _ = controller.CancelAsync());
        menu.Items.Add(new Separator());
        Add(menu, "更换悬浮球形象", () => AppearanceRequested?.Invoke());
        Add(menu, "模型与设置", () => SettingsRequested?.Invoke());
        pauseItem = Add(menu, "暂停快捷键", controller.TogglePause);
        Add(menu, "重置悬浮球位置", () => { Place(true); SavePosition(); });
        menu.Items.Add(new Separator());
        Add(menu, "退出软件", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            origin = PointToScreen(e.GetPosition(this)); windowOrigin = new Point(Left, Top);
            pressed = true; dragged = false; CaptureMouse(); e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (!pressed) return;
            Point current = PointToScreen(e.GetPosition(this));
            Vector delta = current - origin;
            if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance) dragged = true;
            if (!dragged) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = windowOrigin.X + delta.X / dpi.DpiScaleX;
            Top = windowOrigin.Y + delta.Y / dpi.DpiScaleY;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false; ReleaseMouseCapture();
            if (dragged) { Clamp(); SavePosition(); }
            else _ = controller.ToggleAsync();
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => pressed = false;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = NativeMethods.GetWindowLongPtr(handle, -20);
            NativeMethods.SetWindowLongPtr(handle, -20, style | 0x08000000 | 0x00000080);
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return 3; }
                return 0;
            });
            Place(false);
        };
        controller.Changed += Refresh;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => { controller.Changed -= Refresh; SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; };
        Refresh();
    }

    private static MenuItem Add(ContextMenu menu, string text, Action action)
    { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); return item; }

    private void Refresh()
    {
        bool recording = controller.Phase == SessionPhase.Recording;
        bool error = !controller.Busy && controller.Status is "无法开始" or "处理失败" or "输入失败" or "下载失败" or "恢复失败";
        Width = controller.Settings.BallSize; Height = controller.Settings.BallSize + 18;
        Opacity = controller.Settings.BallOpacity;
        label.Text = controller.Settings.OrbStyle switch { "microphone" => "话", "chat" => "聊", _ => "声" };
        stateLabel.Text = controller.Hotkeys.Paused && !controller.Busy ? "暂停" : controller.Phase switch
        {
            SessionPhase.Recording => $"录音 {controller.Seconds:0}s",
            SessionPhase.Recognizing => "识别中",
            SessionPhase.Writing => "验证中",
            SessionPhase.Downloading => $"下载 {controller.DownloadProgress:0}%",
            SessionPhase.Preparing => "准备中",
            SessionPhase.Cancelling => "取消中",
            _ when error => "错误",
            _ => "待机"
        };
        if (loadedImagePath != controller.Settings.OrbImagePath)
        {
            loadedImagePath = controller.Settings.OrbImagePath;
            var image = AppearanceService.TryLoadImage(loadedImagePath);
            avatar.Fill = image == null ? null : AppearanceService.CreateBrush(image, 1, 0, 0);
        }
        avatar.Visibility = avatar.Fill == null ? Visibility.Collapsed : Visibility.Visible;
        label.Visibility = avatar.Fill == null ? Visibility.Visible : Visibility.Collapsed;
        orb.Background = new SolidColorBrush(recording || error ? Color.FromRgb(210, 62, 94) : controller.Busy ? Color.FromRgb(73, 97, 173) : Color.FromRgb(105, 75, 210));
        orb.BorderBrush = new SolidColorBrush(recording || error ? Color.FromRgb(255, 130, 153) : controller.Busy ? Color.FromRgb(140, 180, 255) : Color.FromRgb(196, 181, 253));
        ToolTip = $"{controller.Status}\n{controller.Detail}\n{HotkeyService.Format(controller.Settings.HotkeyModifiers, controller.Settings.HotkeyKey)}\n左键录音 · 拖动移动 · 右键菜单";
        recordItem.Header = recording ? "停止录音" : "开始录音";
        recordItem.IsEnabled = !controller.Busy || recording;
        cancelItem.IsEnabled = controller.Busy;
        pauseItem.Header = controller.Hotkeys.Paused ? "恢复快捷键" : "暂停快捷键";
        if (IsVisible) NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, -1, 0, 0, 0, 0, 0x0013);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { Clamp(); SavePosition(); });
    private void Place(bool reset)
    {
        var area = SystemParameters.WorkArea;
        Left = !reset && controller.Settings.BallLeft is double x ? x : area.Right - Width - 30;
        Top = !reset && controller.Settings.BallTop is double y ? y : area.Top + area.Height * 0.62;
        Clamp();
    }
    private void Clamp()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Point screen = PointToScreen(new Point());
        double x = Math.Clamp(screen.X, area.Left, Math.Max(area.Left, area.Right - Width * dpi.DpiScaleX));
        double y = Math.Clamp(screen.Y, area.Top, Math.Max(area.Top, area.Bottom - Height * dpi.DpiScaleY));
        NativeMethods.SetWindowPos(handle, -1, (int)x, (int)y, 0, 0, 0x0011);
    }
    private void SavePosition() => controller.SavePosition(Left, Top);
}
