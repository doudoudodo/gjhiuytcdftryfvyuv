using System.Windows;

namespace Coclico.Services;

public class LocalizationService
{
    private ResourceDictionary? _currentDict;

    public string CurrentLanguage { get; private set; } = "fr";

    public event Action<string>? LanguageChanged;

    public LocalizationService()
    {
        CurrentLanguage = "fr";
        try
        {
            var uri = new Uri("/Coclico;component/Resources/Lang/fr.xaml", UriKind.Relative);
            _currentDict = new ResourceDictionary { Source = uri };
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
    }

    /// <summary>
    /// Swaps the active language dictionary.
    /// Side effect (deliberate): the chosen language is persisted to settings.json
    /// so it survives restarts — call with the same care as a settings change.
    /// </summary>
    public void SetLanguage(string? langCode)
    {
        try
        {

            // Only languages with an actual dictionary in Resources/Lang/ are accepted
            // (fr.xaml and en.xaml — matches SatelliteResourceLanguages in the csproj).
            string targetCode = langCode?.ToLowerInvariant() == "en" ? "en" : "fr";

            var uri = new Uri($"/Coclico;component/Resources/Lang/{targetCode}.xaml", UriKind.Relative);
            ResourceDictionary? dict = null;

            if (Application.Current != null)
            {
                void SwapDict()
                {
                    dict = new ResourceDictionary { Source = uri };

                    var existingList = Application.Current.Resources.MergedDictionaries
                        .Where(d => (d.Source != null && d.Source.OriginalString.Contains("/Resources/Lang/")) || d == _currentDict)
                        .ToList();
                    foreach (ResourceDictionary? existing in existingList)
                    {
                        _ = Application.Current.Resources.MergedDictionaries.Remove(existing);
                    }

                    Application.Current.Resources.MergedDictionaries.Add(dict);
                }

                if (Application.Current.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(SwapDict);
                }
                else
                {
                    SwapDict();
                }
            }
            else
            {
                try
                {
                    dict = new ResourceDictionary { Source = uri };
                }
                catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            }

            _currentDict = dict;
            CurrentLanguage = targetCode;

            SettingsService? settingsService = ServiceContainer.GetOptional<SettingsService>();
            if (settingsService != null)
            {
                settingsService.Settings.Language = targetCode;
                settingsService.Save();
            }

            LanguageChanged?.Invoke(targetCode);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "LocalizationService.SetLanguage");

            if (langCode != "fr" && CurrentLanguage != "fr")
            {
                try { SetLanguage("fr"); }
                catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            }
        }
    }

    public string Get(string key)
    {
        try
        {
            return Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess()
                ? Application.Current.Dispatcher.Invoke(() => GetInternal(key))
                : GetInternal(key);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "LocalizationService.Get");
        }
        return key;
    }

    private ResourceDictionary? _fallbackDict;

    private string GetInternal(string key)
    {
        if (_currentDict?.Contains(key) == true)
        {
            string? val = _currentDict[key] as string;
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        if (Application.Current?.Resources.Contains(key) == true)
        {
            string? val = Application.Current.Resources[key] as string;
            if (!string.IsNullOrEmpty(val))
            {
                return val;
            }
        }

        if (CurrentLanguage != "fr")
        {
            try
            {
                _fallbackDict ??= new ResourceDictionary { Source = new Uri("/Coclico;component/Resources/Lang/fr.xaml", UriKind.Relative) };
                if (_fallbackDict.Contains(key))
                {
                    string? fallbackVal = _fallbackDict[key] as string;
                    if (!string.IsNullOrEmpty(fallbackVal))
                    {
                        return fallbackVal;
                    }
                }
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        }

        return key;
    }
}
