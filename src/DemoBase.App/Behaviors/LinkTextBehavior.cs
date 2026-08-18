using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace DemoBase.App.Behaviors;

/// <summary>
/// Comportement attaché : transforme automatiquement les URLs (http/https) présentes dans un
/// texte en liens cliquables à l'intérieur d'un TextBlock, qui s'ouvrent dans le navigateur par
/// défaut au clic.
///
/// 2026-07-29, retour utilisateur (message BuildErrorMessage de ReleaseBuilderService, qui
/// inclut désormais l'URL réellement testée dans chaque raison d'échec) : "faudrait pouvoir
/// cliquer dessus et ouvrir le navigateur :P" — un TextBlock lié directement à
/// TextBlock.Text ne permet pas de liens cliquables (Inlines n'est pas bindable), d'où ce
/// comportement attaché qui reconstruit les Inlines (texte brut + Hyperlink) à chaque
/// changement du texte source.
///
/// Usage XAML : &lt;TextBlock behaviors:LinkTextBehavior.AutoLinkText="{Binding MonTexte}" .../&gt;
/// (ne PAS binder TextBlock.Text en parallèle — les deux se marcheraient dessus).
/// </summary>
public static class LinkTextBehavior
{
    public static readonly DependencyProperty AutoLinkTextProperty =
        DependencyProperty.RegisterAttached(
            "AutoLinkText",
            typeof(string),
            typeof(LinkTextBehavior),
            new PropertyMetadata(null, OnAutoLinkTextChanged));

    public static void SetAutoLinkText(DependencyObject element, string? value) =>
        element.SetValue(AutoLinkTextProperty, value);

    public static string? GetAutoLinkText(DependencyObject element) =>
        (string?)element.GetValue(AutoLinkTextProperty);

    // Les messages qui utilisent ce comportement encadrent toujours l'URL entre parenthèses
    // (ex. "... (https://exemple/fichier.zip)") — on exclut donc l'espace ET la parenthèse
    // fermante du match, pour ne pas l'inclure dans le lien.
    private static readonly Regex UrlRegex = new(@"https?://[^\s\)]+", RegexOptions.Compiled);

    private static void OnAutoLinkTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        tb.Inlines.Clear();

        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        int last = 0;
        foreach (Match m in UrlRegex.Matches(text))
        {
            if (m.Index > last)
                tb.Inlines.Add(new Run(text[last..m.Index]));

            var url = m.Value;
            Uri? uri;
            try { uri = new Uri(url); } catch { uri = null; }

            if (uri != null)
            {
                var link = new Hyperlink(new Run(url))
                {
                    NavigateUri     = uri,
                    TextDecorations = TextDecorations.Underline
                };
                // Couleur accent du thème (au lieu du bleu système par défaut, peu lisible sur
                // fond sombre) — DynamicResource pour suivre un changement de thème à chaud.
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                link.RequestNavigate += OnRequestNavigate;
                tb.Inlines.Add(link);
            }
            else
            {
                // URL malformée (ne devrait pas arriver vu le format des messages) — texte brut
                tb.Inlines.Add(new Run(url));
            }

            last = m.Index + m.Length;
        }

        if (last < text.Length)
            tb.Inlines.Add(new Run(text[last..]));
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Non bloquant — pas de navigateur par défaut résolu, ou URL rejetée par l'OS.
        }
        e.Handled = true;
    }
}
