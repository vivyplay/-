using System;
using System.IO;
using System.Text.Json;
using Suisheng;

namespace SuishengTests;

internal static class Stage4Tests
{
    public static void Run(string root)
    {
        string directory = Path.Combine(root, "stage4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(directory);
        bool initiallyAbsent = !store.Exists;
        File.WriteAllText(settingsPath, "{}");
        var upgraded = store.Load();
        Program.Record("旧版设置缺省兼容且首次引导默认未完成", initiallyAbsent && store.Exists && store.LoadWarning == null && !upgraded.OnboardingCompleted);
        upgraded.OnboardingCompleted = true;
        store.Save(upgraded);
        Program.Record("首次引导完成状态持久化", new SettingsStore(directory).Load().OnboardingCompleted);

        const string secret = "DO-NOT-EXPORT-7F31";
        string privateDirectory = Path.Combine(directory, secret);
        var settings = new UserSettings
        {
            ModelDirectory = privateDirectory,
            AiEndpoint = "https://" + secret + ".invalid/chat/completions",
            AiModel = secret,
            AiInstructions = secret,
            ReplacementRules = secret + " => " + secret,
            HistoryEnabled = true,
            OnboardingCompleted = true
        };
        File.WriteAllText(Path.Combine(directory, "history.json"), secret);
        File.WriteAllText(Path.Combine(directory, "ai-credential.bin"), secret);
        string json = DiagnosticExporter.CreateJson(settings, 2, true, true);
        bool whitelist = !json.Contains(secret, StringComparison.Ordinal) &&
            !json.Contains("AiEndpoint", StringComparison.Ordinal) &&
            !json.Contains("history.json", StringComparison.Ordinal) &&
            !json.Contains("ai-credential", StringComparison.Ordinal) &&
            !json.Contains(directory, StringComparison.OrdinalIgnoreCase);
        using (var document = JsonDocument.Parse(json))
        {
            var rootElement = document.RootElement;
            whitelist &= rootElement.GetProperty("schema").GetInt32() == 1 &&
                rootElement.GetProperty("microphoneCount").GetInt32() == 2 &&
                rootElement.GetProperty("model").GetProperty("location").GetString() == "user-selected-directory" &&
                rootElement.GetProperty("model").GetProperty("filePresent").GetBoolean() &&
                rootElement.GetProperty("startupEnabled").GetBoolean();
        }
        Program.Record("诊断白名单不含路径接口正文历史或密钥", whitelist, json);

        string exportPath = Path.Combine(directory, "diagnostics.json");
        File.WriteAllText(exportPath, "old");
        DiagnosticExporter.Export(exportPath, settings, 2, false, false);
        string exported = File.ReadAllText(exportPath);
        Program.Record("诊断导出原子替换且不遗留临时正文", exported.Contains("\"schema\": 1", StringComparison.Ordinal) &&
            !exported.Contains(secret, StringComparison.Ordinal) && !File.Exists(exportPath + ".new"));

        string script = File.ReadAllText(Path.Combine(root, "..", "packaging", "Suisheng.iss"));
        bool installerSafety = script.Contains("PrivilegesRequired=lowest", StringComparison.Ordinal) &&
            script.Contains("AppMutex=Local\\Suisheng.Desktop.0.1", StringComparison.Ordinal) &&
            script.Contains("uninsdeletevalue", StringComparison.Ordinal) &&
            script.Contains("if not UninstallSilent", StringComparison.Ordinal) &&
            script.Contains("ExpandConstant('{localappdata}\\Suisheng')", StringComparison.Ordinal) &&
            script.Contains("MB_YESNO", StringComparison.Ordinal) &&
            !script.Contains("DelTree(ExpandConstant('{app}')", StringComparison.Ordinal);
        Program.Record("安装脚本静态安全约束（非实机安装验收）", installerSafety);
    }
}
