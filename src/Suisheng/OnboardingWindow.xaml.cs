using System;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace Suisheng;

public partial class OnboardingWindow : Window
{
    private readonly SessionController controller;
    private readonly MainWindow panel;
    private int step;
    private bool finished;

    private static readonly (string Heading, string Description, string Action)[] Steps =
    [
        ("先确认麦克风", "选择系统默认麦克风即可开始。点击测试后按界面提示说话；测试只在明确操作时采集音频，停止后不会保存录音。没有麦克风或权限关闭时，软件会给出提示。", "测试麦克风"),
        ("准备本地识别模型", "语音转文字使用本地 Whisper CPU 推理。Base 模型通常更适合中文；安装版已随程序提供 Base 模型。模型不存在时，可在设置中下载或导入，下载过程不会发送音频或识别文字。", "打开模型设置"),
        ("确认全局快捷键", "默认 Ctrl + Alt + Space：按一次开始，再按一次结束。需要修改时，在模型与录音页面点击快捷键框并按下新组合，保存后立即生效；冲突时保留原配置。", "打开快捷键设置"),
        ("认识常驻悬浮球", "左键单击开始或停止，按住拖动可移动，右键打开主面板、设置、AI 开关和退出菜单。录音、识别、优化和错误会显示独立状态文字，不依赖颜色区分。", ""),
        ("做一次安全输入测试", "在空白记事本中放好光标，再使用快捷键或悬浮球。停止后等待识别，检查实际接收文字。不会模拟 Enter 或自动发送；含换行的结果只能复制后手动处理。", "打开语音输入页"),
        ("可选 AI 与数据发送", "AI 默认关闭，不影响本地语音输入。只有启用并确认后，才发送原文、优化要求和模板到你配置的服务，可能产生费用；不发送原始音频。密钥由 Windows DPAPI 当前用户加密，结果必须预览确认后写入。", "查看 AI 配置")
    ];

    public OnboardingWindow(SessionController controller, MainWindow panel)
    {
        this.controller = controller;
        this.panel = panel;
        InitializeComponent();
        controller.Changed += Refresh;
        Closed += (_, _) => controller.Changed -= Refresh;
        Closing += OnClosing;
        Render();
    }

    private void Render()
    {
        var current = Steps[step];
        StepText.Text = $"{step + 1} / {Steps.Length}";
        HeadingText.Text = current.Heading;
        DescriptionText.Text = current.Description;
        ActionButton.Content = current.Action;
        ActionButton.Visibility = current.Action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == Steps.Length - 1 ? "完成引导" : "下一步";
        Refresh();
    }

    private void Refresh()
    {
        StatusText.Text = step switch
        {
            0 => controller.Status + " · " + controller.Detail,
            1 => File.Exists(RecognitionService.ModelPath(controller.Settings)) ? "已发现当前模型文件，首次使用前会校验完整性。" : "当前模型尚未准备好，请在设置中下载或导入。",
            2 => "当前：" + HotkeyService.Format(controller.Settings.HotkeyModifiers, controller.Settings.HotkeyKey),
            5 => controller.Settings.AiEnabled ? "AI 当前已启用。" : "AI 当前关闭，不会发送优化请求。",
            _ => "完成或稍后关闭后，可在主面板“阶段与帮助”中重新打开本引导。"
        };
        ActionButton.IsEnabled = !controller.Busy;
        NextButton.IsEnabled = !controller.Busy;
        SkipButton.IsEnabled = !controller.Busy;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (step == 0) await controller.TestMicrophoneAsync();
        else if (step is 1 or 2) panel.Open(true);
        else if (step == 4) panel.Open();
        else if (step == 5) panel.OpenAiPanel();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (step > 0) step--;
        Render();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (step < Steps.Length - 1) { step++; Render(); }
        else Complete();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        try { controller.CompleteOnboarding(); finished = true; Close(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "引导状态未保存"); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (finished) return;
        try { controller.CompleteOnboarding(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { e.Cancel = true; MessageBox.Show(this, ex.Message, "引导状态未保存"); }
    }
}
