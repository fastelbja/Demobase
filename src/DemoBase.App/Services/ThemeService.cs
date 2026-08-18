using System.Windows;

namespace DemoBase.App.Services;

public enum AppTheme { Dark, Light }

public class ThemeService
{
    private const string DarkSource  = "Themes/DarkTheme.xaml";
    private const string LightSource = "Themes/LightTheme.xaml";

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public void Apply(AppTheme theme)
    {
        CurrentTheme = theme;
        var uri    = new Uri(theme == AppTheme.Dark ? DarkSource : LightSource, UriKind.Relative);
        var merged = Application.Current.Resources.MergedDictionaries;

        // Remplace le dictionnaire de thème actif. Important : on cible
        // précisément DarkSource/LightSource (nom de fichier exact), pas une
        // sous-chaîne comme "Theme" — tous les fichiers du dossier Themes/
        // (Strings.fr.xaml, Styles.xaml) contiennent eux aussi "Theme" dans
        // leur chemin, donc Contains("Theme") matchait le premier fichier du
        // dossier trouvé dans la liste (toujours Strings.fr.xaml en pratique)
        // au lieu du vrai thème actif. Résultat : Light et Dark restaient
        // fusionnés ensemble, et comme la clé dupliquée la plus à droite dans
        // MergedDictionaries gagne, Light écrasait silencieusement Dark sans
        // jamais être retiré.
        var old = merged.FirstOrDefault(d =>
            d.Source != null &&
            (d.Source.OriginalString.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)
          || d.Source.OriginalString.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase)));
        if (old != null) merged.Remove(old);

        merged.Insert(0, new ResourceDictionary { Source = uri });
    }

    public void Toggle() =>
        Apply(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
