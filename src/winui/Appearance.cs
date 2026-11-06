using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace NksTaskMgr.WinUI;

internal enum AppAppearance { System, Light, Dark }

internal static class Appearance
{
    private static readonly string preferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "appearance.txt");
    private static readonly UISettings system = new();
    private static readonly AccessibilitySettings accessibility = new();
    private static DispatcherQueue? dispatcher;
    internal static AppAppearance Preference { get; private set; } = ReadPreference();
    internal static bool IsDark { get; private set; } = ResolveDark();
    internal static bool IsHighContrast { get; private set; } = accessibility.HighContrast;
    internal static ElementTheme Theme => IsDark ? ElementTheme.Dark : ElementTheme.Light;
    internal static event Action? Changed;

    static Appearance()
    {
        // Windows raises this for theme and high contrast changes alike; Apply ignores unchanged states.
        system.ColorValuesChanged += (_, _) => dispatcher?.TryEnqueue(Apply);
    }

    internal static Windows.UI.Color SystemColor(UIElementType type) => system.UIElementColor(type);

    private static bool ResolveDark()
    {
        if (Preference != AppAppearance.System) return Preference == AppAppearance.Dark;
        var color = system.GetColorValue(UIColorType.Background);
        return color.R + color.G + color.B < 384;
    }

    private static void Apply()
    {
        bool dark = ResolveDark(), contrast = accessibility.HighContrast;
        if (IsDark == dark && IsHighContrast == contrast) return;
        IsDark = dark; IsHighContrast = contrast; ThemePalette.Refresh(); Changed?.Invoke();
    }

    internal static bool Select(AppAppearance preference)
    {
        Preference = preference; Apply();
        string temporary = preferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
            File.WriteAllText(temporary, preference.ToString(), new UTF8Encoding(false));
            File.Move(temporary, preferencePath, true);
            return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            // Cleanup must not turn a saved preference into a failed save.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static AppAppearance ReadPreference()
    {
        try {
            using var reader = new StreamReader(preferencePath);
            Span<char> value = stackalloc char[32];
            int length = reader.ReadBlock(value);
            if (reader.EndOfStream && Enum.TryParse<AppAppearance>(value[..length].Trim(), out var preference)
                && Enum.IsDefined(preference)) return preference;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            // Unreadable preferences follow Windows without requiring write access at startup.
        }
        return AppAppearance.System;
    }

    internal static void Attach(Window window, FrameworkElement content)
    {
        dispatcher ??= window.DispatcherQueue;
        Apply();
        void Update() {
            content.RequestedTheme = Theme;
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var bar = window.AppWindow.TitleBar;
            var background = ThemePalette.Color(0x12181c); var foreground = ThemePalette.Color(0x95a4ad);
            bar.BackgroundColor = bar.InactiveBackgroundColor = background;
            bar.ForegroundColor = bar.InactiveForegroundColor = foreground;
            bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = background;
            bar.ButtonForegroundColor = bar.ButtonInactiveForegroundColor = foreground;
            bar.ButtonHoverBackgroundColor = ThemePalette.Color(0x232d34);
            bar.ButtonHoverForegroundColor = ThemePalette.Color(0xe9eef0);
            bar.ButtonPressedBackgroundColor = ThemePalette.Color(0x28343b);
            bar.ButtonPressedForegroundColor = ThemePalette.Color(0xe9eef0);
        }
        Update(); Changed += Update;
        window.Closed += (_, _) => Changed -= Update;
    }

    internal static ContentDialog Dialog(ContentDialog dialog)
    {
        void Update() => dialog.RequestedTheme = Theme;
        Update(); dialog.Opened += (_, _) => { Update(); Changed += Update; };
        dialog.Closed += (_, _) => Changed -= Update;
        return dialog;
    }
}

public sealed partial class MainWindow
{
    private readonly Button appearance = Button(Loc.T("Vzhled"));

    private void ConfigureAppearance()
    {
        Appearance.Attach(this, root);
        appearance.FontSize = 11; appearance.Padding = new Thickness(4, 0, 4, 0);
        appearance.HorizontalAlignment = HorizontalAlignment.Stretch;
        appearance.VerticalAlignment = VerticalAlignment.Bottom; appearance.Margin = new Thickness(8, 0, 8, 12);
        AutomationProperties.SetName(appearance, Loc.T("Vzhled aplikace"));
        AutomationProperties.SetAutomationId(appearance, "appearance-choice");
        ToolTipService.SetToolTip(appearance, Loc.T("Vzhled podle Windows, světlý nebo tmavý"));
        var menu = new MenuFlyout();
        var choices = new Dictionary<AppAppearance, ToggleMenuFlyoutItem>();
        void UpdateChecks() { foreach (var (mode, choice) in choices) choice.IsChecked = mode == Appearance.Preference; }
        foreach (var (mode, label) in new[] { (AppAppearance.System, Loc.T("Podle Windows")), (AppAppearance.Light, Loc.T("Světlý")), (AppAppearance.Dark, Loc.T("Tmavý")) }) {
            var item = new ToggleMenuFlyoutItem { Text = label };
            AutomationProperties.SetAutomationId(item, "appearance-" + mode.ToString().ToLowerInvariant());
            item.Click += (_, _) => {
                bool saved = Appearance.Select(mode); UpdateChecks();
                if (!saved) footer.Text = Loc.T("Vzhled je změněný, ale volbu se nepodařilo uložit pro příští spuštění.");
            };
            choices.Add(mode, item); menu.Items.Add(item);
        }
        var language = new MenuFlyoutSubItem { Text = Loc.T("Jazyk") };
        AutomationProperties.SetAutomationId(language, "language-choice");
        var languages = new Dictionary<string, ToggleMenuFlyoutItem>();
        foreach (var code in Loc.Languages.Prepend(Loc.SystemPreference)) {
            var item = new ToggleMenuFlyoutItem { Text = code == Loc.SystemPreference ? Loc.T("Podle Windows") : Loc.NativeName(code) };
            AutomationProperties.SetAutomationId(item, "language-" + code);
            item.Click += (_, _) => {
                footer.Text = Loc.Select(code) ? Loc.T("Jazyk se změní po restartu aplikace.") : Loc.T("Jazyk se nepodařilo uložit.");
                foreach (var (other, choice) in languages) choice.IsChecked = other == Loc.Preference;
            };
            languages.Add(code, item); language.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(language);
        AddReplacementMenu(menu);
        AddElevationMenu(menu);
        menu.Opening += (_, _) => { UpdateChecks(); foreach (var (code, choice) in languages) choice.IsChecked = code == Loc.Preference; };
        appearance.Flyout = menu; sidebar.Children.Add(appearance);
    }
}
