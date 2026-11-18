using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Suisheng;

public static class ThemeService
{
    public static void Apply(Application app, string theme)
    {
        bool light = theme != "dark";
        if (theme == "system")
        {
            try { light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) as int? != 0; }
            catch { light = true; }
        }
        Set(app, "WindowBackgroundBrush", light ? "#F5F5FA" : "#20202A");
        Set(app, "SurfaceBackgroundBrush", light ? "#FFFFFF" : "#2B2B38");
        Set(app, "TextPrimaryBrush", light ? "#242238" : "#F0EEFA");
        Set(app, "TextSecondaryBrush", light ? "#77738B" : "#B9B5CD");
        Set(app, "ControlBorderBrush", light ? "#DDDCE8" : "#515064");
        Set(app, "AccentBrush", light ? "#7452CF" : "#A78BFA");
        Set(app, "AccentSoftBrush", light ? "#EDE8FF" : "#40345D");
        Set(app, "StatusBackgroundBrush", "#28243C");
    }

    private static void Set(Application app, string key, string color) => app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
