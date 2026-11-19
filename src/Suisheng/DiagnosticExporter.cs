using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Suisheng;

public static class DiagnosticExporter
{
    public static string CreateJson(UserSettings settings, int microphoneCount, bool modelPresent, bool startupEnabled)
    {
        string location = Path.GetFullPath(settings.ModelDirectory).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
            ? "application-directory"
            : Path.GetFullPath(settings.ModelDirectory).StartsWith(Path.GetFullPath(SettingsStore.DataDirectory), StringComparison.OrdinalIgnoreCase)
                ? "default-user-data"
                : "user-selected-directory";
        var snapshot = new
        {
            schema = 1,
            exportedUtc = DateTimeOffset.UtcNow,
            applicationVersion = typeof(DiagnosticExporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
            os = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            microphoneCount,
            model = new { id = settings.ModelId, filePresent = modelPresent, location },
            preferences = new { language = settings.Language, recordingMode = settings.RecordingMode, theme = settings.Theme, silenceDetection = settings.SilenceDetection, historyEnabled = settings.HistoryEnabled, aiEnabled = settings.AiEnabled, onboardingCompleted = settings.OnboardingCompleted },
            startupEnabled
        };
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Export(string path, UserSettings settings, int microphoneCount, bool modelPresent, bool startupEnabled)
    {
        string json = CreateJson(settings, microphoneCount, modelPresent, startupEnabled);
        string temporary = path + ".new";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }
}
