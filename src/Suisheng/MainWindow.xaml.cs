using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Suisheng;

public partial class MainWindow : Window
{
    private readonly SessionController controller;
    private uint modifiers, key;
    private BitmapSource? avatarImage;
    private string? pendingAvatarSource;
    private bool previewDragging;
    private bool updatingAi;
    private Point previewOrigin;
    private double avatarOffsetX, avatarOffsetY;
    public bool AllowClose { get; set; }
    public event Action? ExitRequested;
    public event Action? AiOpenRequested;
    public event Action? OnboardingOpenRequested;

    public MainWindow(SessionController controller)
    {
        this.controller = controller;
        InitializeComponent();
        LoadSettings();
        controller.Changed += Refresh;
        Tabs.SelectionChanged += (_, _) => { if (Tabs.SelectedIndex == 4) RefreshHistory(); };
        Closing += OnClosing;
        Closed += (_, _) => controller.Changed -= Refresh;
        Refresh();
    }

    private void LoadSettings()
    {
        var s = controller.Settings;
        ModelCombo.ItemsSource = ModelInfo.All; ModelCombo.SelectedValue = s.ModelId;
        SourceCombo.SelectedIndex = s.DownloadSource == "mirror" ? 1 : 0;
        LanguageCombo.SelectedIndex = s.Language == "zh" ? 0 : s.Language == "en" ? 1 : 2;
        ModelDirectoryBox.Text = s.ModelDirectory;
        modifiers = s.HotkeyModifiers; key = s.HotkeyKey;
        HotkeyBox.Text = HotkeyService.Format(modifiers, key);
        MaxSecondsBox.Text = s.MaxRecordingSeconds.ToString();
        RecordingModeCombo.SelectedIndex = s.RecordingMode == "hold" ? 1 : 0;
        SilenceDetectionBox.IsChecked = s.SilenceDetection;
        SilenceSecondsBox.Text = s.SilenceWaitSeconds.ToString("0.#");
        AutoPunctuationBox.IsChecked = s.AutoPunctuation;
        BasicFormattingBox.IsChecked = s.BasicFormatting;
        RemoveFillerWordsBox.IsChecked = s.RemoveFillerWords;
        KeepOriginalBox.IsChecked = s.KeepOriginalResult;
        ReplacementRulesBox.Text = s.ReplacementRules;
        ThemeCombo.SelectedIndex = s.Theme == "dark" ? 1 : s.Theme == "system" ? 2 : 0;
        OrbStyleCombo.SelectedIndex = s.OrbStyle == "microphone" ? 1 : s.OrbStyle == "chat" ? 2 : 0;
        AvatarZoomSlider.Value = s.AvatarZoom;
        BallSizeSlider.Value = s.BallSize;
        BallOpacitySlider.Value = s.BallOpacity;
        avatarImage = AppearanceService.TryLoadImage(s.AvatarSourcePath) ?? AppearanceService.TryLoadImage(s.OrbImagePath);
        avatarOffsetX = s.AvatarOffsetX; avatarOffsetY = s.AvatarOffsetY;
        HistoryEnabledBox.IsChecked = s.HistoryEnabled;
        HistoryDaysBox.Text = s.HistoryRetentionDays.ToString();
        UpdateAvatarPreview();
        CloseToTrayBox.IsChecked = s.CloseToTray; StartMinimizedBox.IsChecked = s.StartMinimized;
        try { StartupBox.IsChecked = StartupRegistration.Enabled; }
        catch { StartupBox.IsChecked = false; StartupBox.IsEnabled = false; }
        RefreshDevices();
        RefreshHistory();
    }

    private void Refresh()
    {
        StatusText.Text = controller.Status; DetailText.Text = controller.Detail;
        TimerText.Text = TimeSpan.FromSeconds(controller.Seconds).ToString(@"mm\:ss");
        LevelBar.Value = controller.Level;
        HotkeyText.Text = HotkeyService.Format(controller.Settings.HotkeyModifiers, controller.Settings.HotkeyKey) + (controller.Hotkeys.Paused ? "  ·  已暂停" : controller.Settings.RecordingMode == "hold" ? "  ·  按住说话，松开结束" : "  ·  按一次开始，再按一次结束");
        DeviceText.Text = "当前选择：" + (MicrophoneCombo.SelectedItem is MicrophoneInfo microphone ? microphone.Name : "系统默认麦克风") + " · Whisper 本地 CPU";
        updatingAi = true;
        AiEnabledBox.IsChecked = controller.Settings.AiEnabled; AiSettingsEnabledBox.IsChecked = controller.Settings.AiEnabled;
        updatingAi = false;
        AiStateText.Text = controller.Settings.AiEnabled ? "已开启：识别文字将发送到所配置服务，AI 结果先预览确认；不发送音频。" : "关闭时仅在本地处理，不发送优化请求。";
        if (ResultBox.Text != controller.Result) ResultBox.Text = controller.Result;
        if (OriginalResultBox.Text != controller.OriginalResult) OriginalResultBox.Text = controller.OriginalResult;
        RecordButton.Content = controller.Phase == SessionPhase.Recording ? "停止录音" : "开始录音";
        RecordButton.IsEnabled = !controller.Busy || controller.Phase == SessionPhase.Recording;
        CancelButton.IsEnabled = controller.Busy;
        MicrophoneButton.IsEnabled = !controller.Busy;
        ClearButton.IsEnabled = !controller.Busy && controller.Result.Length > 0;
        CopyButton.IsEnabled = controller.Result.Length > 0;
        RetryButton.IsEnabled = !controller.Busy && controller.Result.Length > 0;
        SettingsPanel.IsEnabled = !controller.Busy;
        SaveButton.IsEnabled = !controller.Busy;
        if (Tabs.SelectedIndex == 4 && !controller.Busy) RefreshHistory();
        DownloadBar.Value = controller.DownloadProgress;
        string path = RecognitionService.ModelPath(controller.Settings);
        ModelStatus.Text = controller.Phase == SessionPhase.Downloading ? $"下载进度 {controller.DownloadProgress:0}% · 可在语音输入页取消" : File.Exists(path) ? "已发现模型文件，使用前会再次校验 SHA-256。" : "尚未准备模型，请下载或导入。";
    }

    public void Open(bool settings = false, bool appearance = false, bool history = false)
    {
        Show(); WindowState = WindowState.Normal; Activate();
        if (settings) Tabs.SelectedIndex = 1;
        if (appearance) { Tabs.SelectedIndex = 3; Dispatcher.BeginInvoke(() => AppearanceHeading.BringIntoView()); }
        if (history) Tabs.SelectedIndex = 4;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        if (controller.Settings.CloseToTray) Hide();
        else ExitRequested?.Invoke();
    }
    private async void Record_Click(object sender, RoutedEventArgs e) => await controller.ToggleAsync();
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await controller.CancelAsync();
    private async void TestMicrophone_Click(object sender, RoutedEventArgs e) => await controller.TestMicrophoneAsync();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;
    internal void OpenAiPanel() => AiOpenRequested?.Invoke();
    private void OpenAi_Click(object sender, RoutedEventArgs e) => OpenAiPanel();
    private void OpenOnboarding_Click(object sender, RoutedEventArgs e) => OnboardingOpenRequested?.Invoke();
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出不含正文和密钥的诊断信息",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "Suisheng-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            int microphones = AudioRecorder.GetDevices().Count;
            bool startup = StartupRegistration.Enabled;
            bool modelPresent = File.Exists(RecognitionService.ModelPath(controller.Settings));
            DiagnosticExporter.Export(dialog.FileName, controller.Settings, microphones, modelPresent, startup);
            MessageBox.Show(this, "诊断信息已导出。文件不包含音频、正文、接口地址、目录路径或密钥。", "导出完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { MessageBox.Show(this, "无法写入所选位置，请检查文件权限后重试。", "导出失败"); }
    }
    private async void AiEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (updatingAi) return;
        bool requested = (sender as CheckBox)?.IsChecked == true;
        try { await controller.SetAiEnabledAsync(requested); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "AI 开关未更改"); AiOpenRequested?.Invoke(); }
        finally { Refresh(); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => controller.ClearResult();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(controller.Result); controller.Notify(controller.Status, "本次文字已复制。此操作按你的要求替换剪贴板内容。"); }
        catch (ExternalException) { MessageBox.Show(this, "剪贴板正被其他软件占用，请稍后重试。", "复制失败"); }
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "点击确定后，请在 5 秒内切换到目标输入框并放好光标。倒计时结束后会重新验证目标；不会发送回车。", "重新选择输入位置", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await controller.RetryWriteAsync();
    }
    private bool SaveSettings()
    {
        try
        {
            if (!int.TryParse(MaxSecondsBox.Text, out int maximum)) throw new ArgumentException("最长录音时间必须是整数。");
            if (!double.TryParse(SilenceSecondsBox.Text, out double silenceSeconds)) throw new ArgumentException("静音等待时间必须是数字。");
            if (!int.TryParse(HistoryDaysBox.Text, out int historyDays)) throw new ArgumentException("历史保留天数必须是整数。");
            var old = controller.Settings;
            var settings = new UserSettings
            {
                HotkeyModifiers = modifiers, HotkeyKey = key,
                ModelId = ModelCombo.SelectedValue as string ?? "base",
                DownloadSource = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "official",
                Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh",
                Microphone = MicrophoneCombo.SelectedValue is int mic ? mic : -1,
                ModelDirectory = ModelDirectoryBox.Text.Trim(), MaxRecordingSeconds = maximum,
                RecordingMode = (RecordingModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "toggle",
                SilenceDetection = SilenceDetectionBox.IsChecked == true, SilenceWaitSeconds = silenceSeconds,
                AutoPunctuation = AutoPunctuationBox.IsChecked == true,
                BasicFormatting = BasicFormattingBox.IsChecked == true,
                RemoveFillerWords = RemoveFillerWordsBox.IsChecked == true,
                KeepOriginalResult = KeepOriginalBox.IsChecked == true,
                ReplacementRules = ReplacementRulesBox.Text,
                Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "light",
                OrbStyle = (OrbStyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wave",
                AvatarSourcePath = old.AvatarSourcePath, OrbImagePath = old.OrbImagePath,
                AvatarZoom = AvatarZoomSlider.Value, AvatarOffsetX = avatarOffsetX, AvatarOffsetY = avatarOffsetY,
                BallSize = BallSizeSlider.Value, BallOpacity = BallOpacitySlider.Value,
                HistoryEnabled = HistoryEnabledBox.IsChecked == true, HistoryRetentionDays = historyDays,
                CloseToTray = CloseToTrayBox.IsChecked == true, StartMinimized = StartMinimizedBox.IsChecked == true,
                BallLeft = old.BallLeft, BallTop = old.BallTop,
                AiEnabled = old.AiEnabled, AiEndpoint = old.AiEndpoint, AiModel = old.AiModel,
                AiTimeoutSeconds = old.AiTimeoutSeconds, AiTemplateId = old.AiTemplateId,
                AiInstructions = old.AiInstructions, AiStrength = old.AiStrength, AiAllowExpansion = old.AiAllowExpansion,
                OnboardingCompleted = old.OnboardingCompleted
            };
            if (avatarImage == null) { settings.OrbImagePath = ""; settings.AvatarSourcePath = ""; }
            else if (pendingAvatarSource != null)
            {
                var assets = AppearanceService.Import(pendingAvatarSource, AvatarZoomSlider.Value, settings.AvatarOffsetX, settings.AvatarOffsetY);
                settings.AvatarSourcePath = assets.SourcePath; settings.OrbImagePath = assets.OrbPath;
            }
            controller.SaveSettings(settings);
            HotkeyHint.Text = "快捷键已保存并立即生效。";
            pendingAvatarSource = null;
            UpdateAvatarPreview();
            RefreshHistory();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or FileFormatException)
        { MessageBox.Show(this, e.Message, "设置未保存"); return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        if (MessageBox.Show(this, "将连接所选模型下载站点，仅下载模型，不发送音频或文字。是否继续？", "下载模型", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await controller.DownloadAsync();
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        var dialog = new OpenFileDialog { Filter = "Whisper 模型 (*.bin)|*.bin", Title = "选择与当前模型一致的 GGML 文件" };
        if (dialog.ShowDialog(this) == true) await controller.ImportModelAsync(dialog.FileName);
    }
    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择模型保存目录" };
        if (dialog.ShowDialog(this) == true) ModelDirectoryBox.Text = dialog.FolderName;
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void RefreshDevices()
    {
        try { MicrophoneCombo.ItemsSource = AudioRecorder.GetDevices(); MicrophoneCombo.SelectedValue = controller.Settings.Microphone; if (MicrophoneCombo.SelectedIndex < 0) MicrophoneCombo.SelectedIndex = 0; }
        catch { controller.Notify("设备读取失败", "请检查 Windows 音频服务和麦克风连接。"); }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { StartupRegistration.Set(StartupBox.IsChecked == true); StartupBox.IsChecked = StartupRegistration.Enabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { StartupBox.IsChecked = StartupRegistration.Enabled; MessageBox.Show(this, "无法修改开机启动项，请检查当前用户权限。", "设置失败"); }
    }
    private void UpdateAvatarPreview()
    {
        AvatarPreview.Fill = avatarImage == null
            ? new SolidColorBrush(OrbStyleCombo.SelectedIndex == 1 ? Color.FromRgb(83, 112, 200) : OrbStyleCombo.SelectedIndex == 2 ? Color.FromRgb(156, 86, 166) : Color.FromRgb(105, 75, 210))
            : AppearanceService.CreateBrush(avatarImage, AvatarZoomSlider.Value, avatarOffsetX, avatarOffsetY);
        AvatarStatus.Text = avatarImage == null ? "正在预览内置形象。保存后生效。" : pendingAvatarSource == null ? "正在预览已保存的形象，可继续调整裁剪后保存。" : "正在预览新图片。图片尚未导入，保存所有设置后复制并生效。";
    }
    private void Appearance_Changed(object sender, RoutedEventArgs e) => UpdateAvatarPreview();
    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "静态图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "选择悬浮球图片（不超过 10 MB）" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var selected = AppearanceService.LoadImage(dialog.FileName);
            avatarImage = selected; pendingAvatarSource = dialog.FileName;
            UpdateAvatarPreview();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or FileFormatException)
        { MessageBox.Show(this, ex.Message, "图片未导入，原形象已保留"); }
    }
    private void ResetAvatar_Click(object sender, RoutedEventArgs e)
    {
        avatarImage = null; pendingAvatarSource = null;
        UpdateAvatarPreview();
    }
    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (avatarImage == null) return;
        previewDragging = true; previewOrigin = e.GetPosition(AvatarPreviewHost);
        AvatarPreviewHost.CaptureMouse(); e.Handled = true;
    }
    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!previewDragging) return;
        Point current = e.GetPosition(AvatarPreviewHost);
        avatarOffsetX = Math.Clamp(avatarOffsetX - (current.X - previewOrigin.X) / 110, -1, 1);
        avatarOffsetY = Math.Clamp(avatarOffsetY - (current.Y - previewOrigin.Y) / 110, -1, 1);
        previewOrigin = current; UpdateAvatarPreview();
    }
    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        previewDragging = false; AvatarPreviewHost.ReleaseMouseCapture();
    }
    private void RefreshHistory()
    {
        Guid selected = (HistoryList.SelectedItem as HistoryEntry)?.Id ?? Guid.Empty;
        var entries = controller.History.GetAll(controller.Settings);
        HistoryList.ItemsSource = entries;
        HistoryList.SelectedItem = entries.FirstOrDefault(item => item.Id == selected);
        if (HistoryList.SelectedItem == null) HistoryDetailBox.Text = "";
        HistoryClearButton.IsEnabled = entries.Count > 0 || controller.History.LoadWarning != null;
        HistoryStatus.Text = controller.History.LoadWarning ?? (controller.Settings.HistoryEnabled ? "历史已开启，仅保存新完成的任务。" : "历史当前关闭，不会写入新记录。");
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = HistoryList.SelectedItem as HistoryEntry;
        HistoryCopyButton.IsEnabled = entry != null;
        HistoryDeleteButton.IsEnabled = entry != null;
        HistoryDetailBox.Text = entry == null ? "" : entry.Text + (entry.Original.Length > 0 ? "\r\n\r\n原始识别：\r\n" + entry.Original : "");
    }
    private void HistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;
        try { Clipboard.SetText(entry.Text); }
        catch (ExternalException) { MessageBox.Show(this, "剪贴板正被其他软件占用，请稍后重试。", "复制失败"); }
    }
    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;
        if (MessageBox.Show(this, "确定删除选中的一条历史记录吗？此操作无法撤销。", "删除历史", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { controller.DeleteHistory(entry.Id); RefreshHistory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "无法保存历史更改，请检查本地目录权限。", "操作失败"); }
    }
    private void HistoryClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定清空全部识别历史吗？仅删除本软件的历史记录，不删除设置、模型、图片或本次结果。此操作无法撤销。", "清空历史", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { controller.ClearHistory(); RefreshHistory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "无法清空历史，请检查本地目录权限。", "操作失败"); }
    }
    private void DefaultHotkey_Click(object sender, RoutedEventArgs e)
    {
        modifiers = 3; key = 0x20; HotkeyBox.Text = HotkeyService.Format(modifiers, key);
        HotkeyHint.Text = "已填入默认快捷键，点击“保存所有设置”后生效。";
    }
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        { HotkeyHint.Text = "请继续按一个普通按键，组成完整快捷键。"; return; }
        if (pressed == Key.Escape)
        {
            modifiers = controller.Settings.HotkeyModifiers; key = controller.Settings.HotkeyKey;
            HotkeyBox.Text = HotkeyService.Format(modifiers, key);
            HotkeyHint.Text = "已取消修改，保留当前快捷键。"; return;
        }
        if (pressed is Key.Return or Key.Tab)
        { HotkeyHint.Text = "Enter 和 Tab 不用于录音快捷键，请换用字母、数字、空格或功能键。"; return; }
        ModifierKeys held = Keyboard.Modifiers;
        if (held == ModifierKeys.None)
        { HotkeyHint.Text = "单独按键容易误触，请同时按住 Ctrl、Alt、Shift 或 Win 中的一项。"; return; }
        modifiers = (uint)held; key = (uint)KeyInterop.VirtualKeyFromKey(pressed);
        HotkeyBox.Text = HotkeyService.Format(modifiers, key);
        HotkeyHint.Text = "新组合已录制，点击“保存所有设置”后立即生效；若被占用，原快捷键保持不变。";
    }
}
