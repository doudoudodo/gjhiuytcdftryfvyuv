using System.Windows;
using System.Windows.Media;

using Microsoft.Win32;

using Wpf.Ui.Appearance;

namespace Coclico.Services;

public class ThemeService : IDisposable
{
    private int _saveGeneration;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // GetOptional: this system event can fire before the DI container is built.
        SettingsService? settings = ServiceContainer.GetOptional<SettingsService>();
        if (settings == null ||
            !string.Equals(settings.Settings.BackgroundMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = (Application.Current?.Dispatcher.InvokeAsync(ApplyCurrentSettings));
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Flush any debounced save: closing the app within the debounce window
        // must not lose the last theme change.
        try { ServiceContainer.GetRequired<SettingsService>().Save(); }
        catch { /* best effort during shutdown */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Debounced settings save: slider dragging fires Apply* many times per
    /// second, and writing settings.json on every call was a save storm.
    /// The last change within the window triggers a single write; Dispose()
    /// flushes whatever is pending at shutdown.
    /// </summary>
    private void SaveSettingsDebounced()
    {
        int generation = Interlocked.Increment(ref _saveGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _saveGeneration, generation, generation) != generation)
            {
                return; // a newer change is pending and will do the save
            }

            try
            {
                ServiceContainer.GetRequired<SettingsService>().Save();
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "ThemeService.SaveSettingsDebounced");
            }
        });
    }

    public void ApplyCurrentSettings()
    {
        AppSettings settings = ServiceContainer.GetRequired<SettingsService>().Settings;

        string bgMode = settings.BackgroundMode ?? "UltraDark";
        if (string.Equals(bgMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            bgMode = IsSystemUsingLightTheme() ? "Light" : "Dark";
        }

        ApplicationThemeManager.Apply(string.Equals(bgMode, "Light", StringComparison.OrdinalIgnoreCase)
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark);

        ApplyBackground(settings.BackgroundMode!);
        ApplyAccentColor(settings.AccentColor);
        ApplyCardOpacity(settings.CardOpacity);
        ApplyCompactMode(settings.CompactMode);
        Application.Current.Resources["GlobalFontSize"] = settings.FontSize;
    }

    // Static palette catalog (testable, no logic in the switch).
    private static readonly Dictionary<string, string> PresetPalette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coquelicot"] = "#FF453A",
        ["RedSun"] = "#FF453A",
        ["Sunset"] = "#FF6B35",
        ["Amber"] = "#FFA000",
        ["Emerald"] = "#10B981",
        ["Rose"] = "#F43F5E",
        ["Cyan"] = "#39C6C0",
    };

    public void ApplyPreset(string preset)
    {
        if (!PresetPalette.TryGetValue(preset, out string? color))
        {
            color = "#FF453A";
        }

        SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
        ss.Settings.ThemePreset = preset;
        ss.Settings.AccentColor = color;
        ApplyAccentColor(color);
        SaveSettingsDebounced();
    }

    public void ApplyAccentColor(string hexColor)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();

            byte r = (byte)Math.Min(255, color.R + 40);
            byte g = (byte)Math.Min(255, color.G + 40);
            byte b = (byte)Math.Min(255, color.B + 40);
            var lighterColor = Color.FromArgb(color.A, r, g, b);
            var lighterBrush = new SolidColorBrush(lighterColor);
            lighterBrush.Freeze();

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0.5)
            };
            gradient.GradientStops.Add(new GradientStop(color, 0));
            gradient.GradientStops.Add(new GradientStop(lighterColor, 0.55));
            gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FFA000"), 1));
            gradient.Freeze();

            Application.Current.Resources["AccentPrimary"] = color;
            Application.Current.Resources["AccentSecondary"] = lighterColor;
            Application.Current.Resources["PrimaryBrush"] = brush;
            Application.Current.Resources["SecondaryBrush"] = lighterBrush;
            Application.Current.Resources["AccentPrimaryBrush"] = brush;
            var borderSubtle = new SolidColorBrush(Color.FromRgb(42, 38, 46));
            borderSubtle.Freeze();
            Application.Current.Resources["BorderAccentBrush"] = borderSubtle;
            Application.Current.Resources["ArchitecturalYellowBrush"] = brush;
            Application.Current.Resources["PrimaryGradient"] = gradient;

            SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
            ss.Settings.AccentColor = hexColor;
            SaveSettingsDebounced();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.ApplyAccentColor");
        }
    }

    public void ApplyBackground(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            mode = "UltraDark";
        }

        string preferenceToSave = mode;

        if (string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase))
        {
            mode = IsSystemUsingLightTheme() ? "Light" : "Dark";
        }

        (string? bgHex, string? panelHex, string? cardHex, string? elevatedHex, string? hoverHex, string? borderHex, string? subtleBorderHex, string? textPrimary, string? textSecondary, string? textMuted, string? textHint) = mode switch
        {
            "Light" => ("#FBFBFB", "#F2F0ED", "#FFFFFF", "#FFFFFF", "#EBE7E2", "#DDD6CD", "#EBE5DC", "#151311", "#4A433D", "#786E66", "#998E84"),
            "Dark" => ("#090A0F", "#0F1117", "#14161F", "#1A1D28", "#242735", "#2C2628", "#1B171A", "#FFFFFF", "#D2CBC6", "#9C928B", "#706760"),
            "Midnight" or "Obsidian" => ("#060709", "#0A0B0F", "#101117", "#161821", "#1E202B", "#261D1F", "#161214", "#FFFFFF", "#D2CBC6", "#9C928B", "#706760"),
            _ => ("#060709", "#0A0B0F", "#101117", "#161821", "#1E202B", "#261D1F", "#161214", "#FFFFFF", "#D2CBC6", "#9C928B", "#706760")
        };

        try
        {
            var bgColor = (Color)ColorConverter.ConvertFromString(bgHex);
            var cardColor = (Color)ColorConverter.ConvertFromString(cardHex);

            SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
            double opacity = Math.Clamp(ss.Settings.BackgroundOpacity, SettingsService.ThemeOpacityBounds.BackgroundMin, SettingsService.ThemeOpacityBounds.BackgroundMax);
            var bgWithAlpha = Color.FromArgb((byte)Math.Round(opacity * 255), bgColor.R, bgColor.G, bgColor.B);

            var bgBrush = new SolidColorBrush(bgWithAlpha); bgBrush.Freeze();
            var cardBrush = new SolidColorBrush(cardColor); cardBrush.Freeze();

            Application.Current.Resources["BgBaseBrush"] = bgBrush;
            Application.Current.Resources["BgPanelBrush"] = CreateBrush(panelHex);
            Application.Current.Resources["BgSidebarBrush"] = CreateBrush(panelHex);
            Application.Current.Resources["BgSurfaceBrush"] = CreateBrush(panelHex);
            Application.Current.Resources["BgCardBrush"] = cardBrush;
            Application.Current.Resources["BgElevatedBrush"] = CreateBrush(elevatedHex);
            Application.Current.Resources["BgHoverBrush"] = CreateBrush(hoverHex);
            Application.Current.Resources["BorderDefaultBrush"] = CreateBrush(borderHex);
            Application.Current.Resources["BorderSubtleBrush"] = CreateBrush(subtleBorderHex);
            Application.Current.Resources["BgDarkColor"] = bgWithAlpha;
            Application.Current.Resources["BgCardColor"] = cardColor;
            Application.Current.Resources["BgDark"] = bgBrush;
            Application.Current.Resources["BgCard"] = cardBrush;

            Application.Current.Resources["BorderStrongBrush"] = CreateBrush(mode == "Light" ? "#D4C7BF" : "#4A3337");

            var textPrimaryBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textPrimary));
            textPrimaryBrush.Freeze();
            var textMutedBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textMuted));
            textMutedBrush.Freeze();

            Application.Current.Resources["TextPrimaryBrush"] = textPrimaryBrush;
            Application.Current.Resources["TextSecondaryBrush"] = CreateBrush(textSecondary);
            Application.Current.Resources["TextMutedBrush"] = textMutedBrush;
            Application.Current.Resources["TextHintBrush"] = CreateBrush(textHint);

            ss.Settings.BackgroundMode = preferenceToSave;
            SaveSettingsDebounced();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.ApplyBackground");
        }
    }

    public void ApplyBackgroundOpacity(double opacity)
    {
        try
        {
            opacity = Math.Clamp(opacity, SettingsService.ThemeOpacityBounds.BackgroundMin, SettingsService.ThemeOpacityBounds.BackgroundMax);
            SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
            ss.Settings.BackgroundOpacity = opacity;
            SaveSettingsDebounced();

            ApplyBackground(ss.Settings.BackgroundMode);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.ApplyBackgroundOpacity");
        }
    }

    public void ApplyCardOpacity(double opacity)
    {
        try
        {
            opacity = Math.Clamp(opacity, SettingsService.ThemeOpacityBounds.CardMin, SettingsService.ThemeOpacityBounds.CardMax);

            double factor = Math.Clamp(opacity / SettingsService.ThemeOpacityBounds.CardMax, 0.0, 1.0);
            int baseR = 14; int baseG = 16; int baseB = 22;
            int targetR = 22; int targetG = 24; int targetB = 32;
            byte r = (byte)Math.Round(baseR + (factor * (targetR - baseR)));
            byte g = (byte)Math.Round(baseG + (factor * (targetG - baseG)));
            byte b = (byte)Math.Round(baseB + (factor * (targetB - baseB)));

            var cardColor = Color.FromRgb(r, g, b);
            var cardBrush = new SolidColorBrush(cardColor);
            cardBrush.Freeze();

            Application.Current.Resources["BgCardBrush"] = cardBrush;
            Application.Current.Resources["BgCardColor"] = cardColor;
            Application.Current.Resources["BgCard"] = cardBrush;

            SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
            ss.Settings.CardOpacity = opacity;
            SaveSettingsDebounced();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.ApplyCardOpacity");
        }
    }

    public void ApplyCompactMode(bool compact)
    {
        try
        {
            Application.Current.Resources.Remove("CardPadding");
            Application.Current.Resources["CardPadding"] = compact
                ? new Thickness(12)
                : new Thickness(24);

            SettingsService ss = ServiceContainer.GetRequired<SettingsService>();
            Application.Current.Resources["GlobalFontSize"] = compact
                ? 11.5
                : ss.Settings.FontSize;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.SetCompactMode(compact);
                }
            });
            ss.Settings.CompactMode = compact;
            SaveSettingsDebounced();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.ApplyCompactMode");
        }
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private bool IsSystemUsingLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            if (key is null)
            {
                return false;
            }

            object? value = key.GetValue("AppsUseLightTheme");
            return value is int intVal && intVal != 0;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ThemeService.IsSystemUsingLightTheme");
            return false;
        }
    }
}
