using System.Windows;

namespace DemoBase.App.Services;

public class LocalizationService
{
    private const string FrSource = "Themes/Strings.fr.xaml";
    private const string EnSource = "Themes/Strings.en.xaml";

    public string CurrentLanguage { get; private set; } = "fr";

    /// <summary>Accessible statiquement depuis les services sans injection (ex. import).</summary>
    public static string CurrentLanguageStatic { get; private set; } = "fr";

    /// <summary>Déclenché après chaque changement de langue.</summary>
    public event Action? LanguageChanged;

    public void Apply(string language)
    {
        CurrentLanguage = language.ToLowerInvariant() switch
        {
            "en" or "english" => "en",
            _                 => "fr",
        };
        CurrentLanguageStatic = CurrentLanguage;

        var source  = CurrentLanguage == "en" ? EnSource : FrSource;
        var uri     = new Uri(source, UriKind.Relative);
        var merged  = Application.Current.Resources.MergedDictionaries;

        // Remplacer le dictionnaire de chaînes existant
        var old = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Strings.") == true);
        if (old != null) merged.Remove(old);

        merged.Add(new ResourceDictionary { Source = uri });

        LanguageChanged?.Invoke();
    }

    /// <summary>Récupère une chaîne localisée depuis les ressources WPF.</summary>
    public static string Get(string key)
    {
        return Application.Current.TryFindResource(key) as string ?? key;
    }

    public void Toggle() =>
        Apply(CurrentLanguage == "fr" ? "en" : "fr");
}
