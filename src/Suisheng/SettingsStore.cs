using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Suisheng;

public sealed class UserSettings
{
    public uint HotkeyModifiers { get; set; } = 3;
    public uint HotkeyKey { get; set; } = 0x20;
    public int Microphone { get; set; } = -1;
    public string Language { get; set; } = "zh";
    public string ModelDirectory { get; set; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "models", "ggml-base.bin"))
        ? Path.Combine(AppContext.BaseDirectory, "models") : Path.Combine(SettingsStore.DataDirectory, "models");
    public string ModelId { get; set; } = "base";
    public string DownloadSource { get; set; } = "official";
    public string RecordingMode { get; set; } = "toggle";
    public bool SilenceDetection { get; set; }
    public double SilenceWaitSeconds { get; set; } = 2;
    public bool AutoPunctuation { get; set; } = true;
    public bool BasicFormatting { get; set; } = true;
    public bool RemoveFillerWords { get; set; }
    public string ReplacementRules { get; set; } = "";
    public bool KeepOriginalResult { get; set; } = true;
    public string Theme { get; set; } = "light";
    public string OrbStyle { get; set; } = "wave";
    public string AvatarSourcePath { get; set; } = "";
    public string OrbImagePath { get; set; } = "";
    public double AvatarZoom { get; set; } = 1;
    public double AvatarOffsetX { get; set; }
    public double AvatarOffsetY { get; set; }
    public double BallSize { get; set; } = 82;
    public double BallOpacity { get; set; } = 1;
    public bool HistoryEnabled { get; set; }
    public int HistoryRetentionDays { get; set; } = 30;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int MaxRecordingSeconds { get; set; } = 120;
    public double? BallLeft { get; set; }
    public double? BallTop { get; set; }
    public bool AiEnabled { get; set; }
    public string AiEndpoint { get; set; } = "";
    public string AiModel { get; set; } = "";
    public int AiTimeoutSeconds { get; set; } = 60;
    public string AiTemplateId { get; set; } = "general";
    public string AiInstructions { get; set; } = "纠正识别错误，保留原意。";
    public string AiStrength { get; set; } = "balanced";
    public bool AiAllowExpansion { get; set; }
    public UserSettings Clone() => (UserSettings)MemberwiseClone();
}

public sealed class SettingsStore
{
    public static string DataDirectory
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("SUISHENG_DATA_DIRECTORY");
            return !string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath)
                ? overridePath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Suisheng");
        }
    }
    private readonly string path;
    public string? LoadWarning { get; private set; }
    public SettingsStore(string? root = null) => path = Path.Combine(root ?? DataDirectory, "settings.json");

    public UserSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new();
            Validate(settings);
            return settings;
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException)
        {
            LoadWarning = "设置文件无法读取，已使用默认值；原文件未删除。";
            return new();
        }
    }

    public static void Validate(UserSettings settings)
    {
        settings.AiEndpoint ??= ""; settings.AiModel ??= ""; settings.AiInstructions ??= ""; settings.AiTemplateId ??= "general"; settings.AiStrength ??= "balanced";
        if (settings.MaxRecordingSeconds is < 5 or > 600) throw new ArgumentException("最长录音时间须为 5–600 秒。");
        if (settings.Language is not ("zh" or "en" or "auto")) throw new ArgumentException("不支持的识别语言。");
        if (settings.DownloadSource is not ("official" or "mirror")) throw new ArgumentException("不支持的下载源。");
        if (settings.ModelId is not ("tiny-q5_1" or "base")) throw new ArgumentException("不支持的模型。");
        if (!Path.IsPathFullyQualified(settings.ModelDirectory)) throw new ArgumentException("请选择绝对路径的模型目录。");
        if (settings.Microphone < -1) throw new ArgumentException("无效的麦克风编号。");
        if (settings.RecordingMode is not ("toggle" or "hold")) throw new ArgumentException("不支持的录音模式。");
        if (settings.SilenceWaitSeconds is < 0.5 or > 10 || !double.IsFinite(settings.SilenceWaitSeconds)) throw new ArgumentException("静音等待时间须为 0.5–10 秒。");
        if (settings.Theme is not ("light" or "dark" or "system")) throw new ArgumentException("不支持的主题。");
        if (settings.OrbStyle is not ("wave" or "microphone" or "chat")) throw new ArgumentException("不支持的内置形象。");
        if (settings.AvatarZoom is < 1 or > 4 || !double.IsFinite(settings.AvatarZoom)) throw new ArgumentException("图片缩放须为 1–4 倍。");
        if (settings.AvatarOffsetX is < -1 or > 1 || !double.IsFinite(settings.AvatarOffsetX) || settings.AvatarOffsetY is < -1 or > 1 || !double.IsFinite(settings.AvatarOffsetY)) throw new ArgumentException("图片位置超出允许范围。");
        if (settings.BallSize is < 48 or > 160 || !double.IsFinite(settings.BallSize)) throw new ArgumentException("悬浮球大小须为 48–160。");
        if (settings.BallOpacity is < 0.4 or > 1 || !double.IsFinite(settings.BallOpacity)) throw new ArgumentException("悬浮球透明度须为 40%–100%。");
        if (settings.HistoryRetentionDays is < 1 or > 3650) throw new ArgumentException("历史保留期限须为 1–3650 天。");
        if (settings.AiTimeoutSeconds is < 5 or > 300) throw new ArgumentException("AI 请求超时须为 5–300 秒。");
        if (settings.AiStrength is not ("light" or "balanced" or "thorough")) throw new ArgumentException("不支持的 AI 优化强度。");
        if (settings.AiEndpoint.Length > 2048 || settings.AiModel.Length > 200 || settings.AiInstructions.Length > 4000 || settings.AiTemplateId.Length > 100)
            throw new ArgumentException("AI 配置文字超出允许长度。");
        if (settings.AiEndpoint.Length > 0)
        {
            if (!Uri.TryCreate(settings.AiEndpoint, UriKind.Absolute, out var endpoint) || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new ArgumentException("AI 接口地址必须是完整的绝对 URL，且不能包含用户信息或片段。");
            bool loopback = endpoint.Host is "localhost" or "127.0.0.1" or "::1";
            if (endpoint.Scheme != Uri.UriSchemeHttps && !(loopback && endpoint.Scheme == Uri.UriSchemeHttp))
                throw new ArgumentException("公网 AI 接口必须使用 HTTPS；HTTP 仅允许本机回环地址。");
        }
        if (settings.AiEnabled && (string.IsNullOrWhiteSpace(settings.AiEndpoint) || string.IsNullOrWhiteSpace(settings.AiModel)))
            throw new ArgumentException("开启 AI 前必须填写接口地址和模型名称。");
        TextProcessingService.ParseReplacements(settings.ReplacementRules);
        if (!string.IsNullOrEmpty(settings.OrbImagePath) && !Path.IsPathFullyQualified(settings.OrbImagePath)) throw new ArgumentException("悬浮球图片必须使用绝对路径。");
        if (!string.IsNullOrEmpty(settings.AvatarSourcePath) && !Path.IsPathFullyQualified(settings.AvatarSourcePath)) throw new ArgumentException("原始形象图片必须使用绝对路径。");
        if (settings.HotkeyModifiers == 0 || (settings.HotkeyModifiers & ~15u) != 0 || settings.HotkeyKey is < 0x20 or > 0xFE)
            throw new ArgumentException("请使用包含修饰键的有效快捷键。");
        if (settings.BallLeft is double x && !double.IsFinite(x)) settings.BallLeft = null;
        if (settings.BallTop is double y && !double.IsFinite(y)) settings.BallTop = null;
    }

    public void Save(UserSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public static class StartupRegistration
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            var expected = $"\"{Environment.ProcessPath}\" --background";
            return string.Equals(key?.GetValue("Suisheng") as string, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) key.SetValue("Suisheng", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Suisheng", false);
    }
}
