using CommunityToolkit.Mvvm.ComponentModel;
using DemoBase.Core.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace DemoBase.App.Views.Releases;

/// <summary>Une ligne de la fenêtre de choix de fichier — le DatEntry lui-même, le nom
/// du .zip affiché, et une estimation du libellé de plateforme concernée (basée sur un
/// override déjà enregistré pour ce fichier, ou à défaut un rapprochement du dossier du
/// RomPath avec les plateformes taguées sur la release) — voir
/// ReleaseDetailViewModel.BuildFilePickerEntriesAsync.</summary>
public record FilePickerEntry(DatEntry Entry, string FileName, string PlatformLabel)
{
    /// <summary>
    /// Contenu de l'archive .zip (nom + taille de chaque fichier attendu, d'après le DAT)
    /// affiché en tooltip sur la ligne correspondante (2026-07-25, retour utilisateur) —
    /// pratique pour distinguer deux fichiers au nom quasi identique sans avoir à ouvrir
    /// l'archive.
    /// </summary>
    public string ContentsTooltip =>
        Entry.Roms.Count == 0
            ? DemoBase.App.Services.LocalizationService.Get("FPick_EmptyArchive")
            : string.Join("\n", Entry.Roms
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => $"{r.Name}  ({FormatSize(r.Size)})"));

    // 2026-07-25 : unités traduites (voir aussi ImportProgressWindow.FormatBytes, qui
    // lui reste volontairement en "B/KB/MB" universel — ici on garde "o/Ko/Mo" en
    // français car ce sont les unités affichées ailleurs dans l'appli côté fichiers).
    private static string FormatSize(long size)
    {
        if (size < 1024)
            return $"{size} {DemoBase.App.Services.LocalizationService.Get("FPick_UnitBytes")}";
        if (size < 1024 * 1024)
            return $"{size / 1024.0:F1} {DemoBase.App.Services.LocalizationService.Get("FPick_UnitKB")}";
        return $"{size / (1024.0 * 1024):F1} {DemoBase.App.Services.LocalizationService.Get("FPick_UnitMB")}";
    }
}

/// <summary>
/// Fenêtre modale de choix de FICHIER — affichée par
/// ReleaseDetailViewModel.LaunchAsync quand une release a plusieurs fichiers (DatEntry)
/// lançables et qu'aucun n'a encore été explicitement sélectionné pour cette session ni
/// mémorisé comme préféré (2026-07-25, retour utilisateur : "Starstruck", Amiga AGA +
/// Atari Falcon, 4 fichiers — cf. RESUME_PROJET.md). Le choix est mémorisé
/// (ReleasePreferredFiles) pour ne plus jamais être redemandé pour cette release.
/// </summary>
public partial class FilePickerWindow : Window
{
    public FilePickerViewModel Vm { get; }

    /// <summary>Fichier choisi par l'utilisateur, ou null si annulé (vérifier
    /// DialogResult == true avant de lire cette propriété).</summary>
    public DatEntry? SelectedFile => Vm.SelectedEntry?.Entry;

    public FilePickerWindow(IEnumerable<FilePickerEntry> entries)
    {
        InitializeComponent();
        Vm = new FilePickerViewModel(entries);
        DataContext = Vm;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedEntry == null)
        {
            // 2026-07-25 : remplace la MessageBox.Show native (jamais traduite,
            // hors charte graphique) par SimpleInfoDialog.
            new DemoBase.App.Views.SimpleInfoDialog(
                "FPick_NoneSelected_Title", "FPick_NoneSelected_Heading", "FPick_NoneSelected_Body")
            { Owner = this }.ShowDialog();
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}

/// <summary>DataContext minimal de FilePickerWindow — pas de service injecté, juste de
/// quoi porter les bindings (liste de fichiers, sélection).</summary>
public partial class FilePickerViewModel : ObservableObject
{
    public ObservableCollection<FilePickerEntry> Entries { get; }

    [ObservableProperty] private FilePickerEntry? _selectedEntry;

    public FilePickerViewModel(IEnumerable<FilePickerEntry> entries)
    {
        Entries      = new ObservableCollection<FilePickerEntry>(entries);
        SelectedEntry = Entries.FirstOrDefault();
    }
}
