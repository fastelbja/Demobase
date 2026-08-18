using DemoBase.App.ViewModels;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.UI.Controls;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Releases;

public partial class SoundtrackPlayerView : UserControl
{
    private readonly SoundtrackPlayerViewModel _vm;
    private FullPatternWindow?                 _fullPatternWindow;
    private DemoBase.App.Controls.ExeMusicHostControl? _exeHost;

    // 2026-07-31, retour utilisateur ("peux tu le mettre aussi sur la vue principale ?") :
    // même panneau Infos (ordre + instruments) que FullPatternWindow.xaml.cs, dupliqué ici
    // plutôt que factorisé en contrôle partagé — les deux vues ont des ViewModels/hôtes
    // distincts et cette page n'a pas de compilateur .NET disponible pour valider un
    // refactor de contrôle partagé en toute sécurité ; la duplication reste contenue
    // (une seule méthode de rendu par fichier) et suit un style déjà présent ailleurs dans
    // ce projet (cf. les deux switches TrackerStyle dans SoundtrackPlayerViewModel.cs).
    // 2026-07-31 (suite) : "ça me plait bien ce petit panneau d'info. affiche le par
    // défaut stp." — ouvert par défaut désormais.
    // 2026-08-02, retour utilisateur ("la partie info à droite ne devrait pas apparaitre
    // lors de la vue oscilloscope" + "garde l'affiche de l'info dans les préférences de
    // sorte à ce que l'utilisateur puisse choisir") : _infoPanelOpen reflète maintenant le
    // CHOIX utilisateur (persisté via PreferencesService.LastInfoPanelOpen/
    // SetInfoPanelOpenAsync), initialisé depuis ce cache statique plutôt que codé en dur à
    // true — mais l'affichage réel (cf. UpdateInfoPanelVisibility) exige EN PLUS
    // Vm.HasPatterns : un fichier sans patterns (MP3, vue oscilloscope) ne montrerait de
    // toute façon qu'un "Ordre (0 positions, 0 patterns)"/"Samples (0)" vide.
    private bool _infoPanelOpen = DemoBase.Data.PreferencesService.LastInfoPanelOpen;
    private const double InfoPanelWidth = 220.0;

    public SoundtrackPlayerViewModel Vm => _vm;

    public SoundtrackPlayerView(ITrackerService trackerService)
    {
        InitializeComponent();
        _vm        = new SoundtrackPlayerViewModel(trackerService);
        DataContext = _vm;

        // 2026-08-02 : état initial du panneau Infos — le XAML le déclare Visible/220
        // par défaut (avant tout fichier ouvert, donc HasPatterns=false) ; on resynchronise
        // ici avec la préférence utilisateur ET l'absence de patterns au démarrage.
        UpdateInfoPanelVisibility();

        // Brancher l'oscilloscope quand SampleBuffer change (nouveau player chargé)
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.SampleBuffer))
                OscilloscopeView.SampleBuffer = _vm.SampleBuffer;
            // 2026-08-07 : même mécanisme que SampleBuffer ci-dessus, pour la vue
            // d'ensemble de la forme d'onde sous l'oscilloscope.
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.WaveformOverview))
                WaveformOverviewControl.WaveformOverview = _vm.WaveformOverview;
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.ChannelLevels))
                PatternViewControl.ForceRender();
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.IsExeMusic))
                ExeHostContainer.Visibility = _vm.IsExeMusic
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.ExeOutput))
                ExeOutputScroller?.ScrollToEnd();
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.Module) && _infoPanelOpen)
                RefreshInfoPanelContent();
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.CurrentOrderIndex))
                RefreshInfoPanelHighlight();
            // 2026-08-02, retour utilisateur ("la partie info à droite ne devrait pas
            // apparaitre lors de la vue oscilloscope") : HasPatterns passe à false dès
            // qu'un fichier sans patterns (MP3, vue oscilloscope) est chargé — masquer
            // le panneau dans ce cas, indépendamment du choix _infoPanelOpen, sans le
            // perdre (il réapparaît sur le prochain fichier tracker si _infoPanelOpen
            // est toujours true).
            if (e.PropertyName == nameof(SoundtrackPlayerViewModel.HasPatterns))
                UpdateInfoPanelVisibility();
        };

        _vm.ExeMusicWindowReady += OnExeMusicWindowReady;

        Unloaded += (_, _) =>
        {
            _vm.Dispose();
            _fullPatternWindow?.Close();
            _fullPatternWindow = null;
            DetachExeHost();
        };
    }

    // ── Panneau "Infos" (ordre / patterns / instruments) ───────────────────────
    // Même logique que FullPatternWindow.xaml.cs (RefreshInfoPanelContent/Highlight) —
    // voir son commentaire pour le détail du choix Instruments vs Samples et de la
    // limite "pas de taille exposée par libopenmpt".

    private async void BtnToggleInfo_Click(object sender, RoutedEventArgs e)
    {
        _infoPanelOpen = !_infoPanelOpen;
        UpdateInfoPanelVisibility();
        // 2026-08-02, retour utilisateur ("garde l'affiche de l'info dans les
        // préférences de sorte à ce que l'utilisateur puisse choisir") : persisté en
        // fire-and-forget côté UI (simple préférence d'affichage, pas bloquant).
        if (DemoBase.Data.PreferencesService.Instance is { } prefs)
            await prefs.SetInfoPanelOpenAsync(_infoPanelOpen);
    }

    /// <summary>Applique l'état réel (Width/Visibility) du panneau Infos à partir du
    /// choix utilisateur (_infoPanelOpen) ET de Vm.HasPatterns — un fichier sans
    /// patterns (MP3 par ex., vue oscilloscope) n'a rien à montrer dans ce panneau
    /// ("Ordre (0 positions, 0 patterns)"/"Samples (0)" vides), donc il reste masqué
    /// même si _infoPanelOpen est true, sans que ce choix soit perdu pour autant.</summary>
    private void UpdateInfoPanelVisibility()
    {
        bool show = _infoPanelOpen && _vm.HasPatterns;
        MainInfoPanelColumn.Width     = new GridLength(show ? InfoPanelWidth : 0);
        MainInfoPanelBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) RefreshInfoPanelContent();
    }

    private void RefreshInfoPanelContent()
    {
        var module = _vm.Module;

        var orderItems = new List<string>();
        if (module != null)
        {
            for (int ord = 0; ord < module.OrderList.Count; ord++)
                orderItems.Add($"{ord:000} → Pattern {module.OrderList[ord]:00}");
        }
        MainOrderListBox.ItemsSource = orderItems;
        MainOrderListHeader.Text = $"Ordre ({orderItems.Count} positions, " +
                                    $"{module?.Patterns.Count ?? 0} patterns)";

        var instrItems = new List<string>();
        string instrLabel;
        if (module != null && module.Instruments.Count > 0)
        {
            instrLabel = "Instruments";
            foreach (var instr in module.Instruments)
                instrItems.Add($"{instr.Index + 1:00}: {instr.Name}");
        }
        else
        {
            instrLabel = "Samples";
            if (module != null)
                foreach (var smp in module.Samples)
                    instrItems.Add($"{smp.Index + 1:00}: {smp.Name}");
        }
        MainInstrumentsListBox.ItemsSource = instrItems;
        MainInstrumentsHeader.Text = $"{instrLabel} ({instrItems.Count})";
        // 2026-07-31, retour utilisateur ("quand le player joue le morceau suivant, peux
        // tu remonter le scrollbar des 'samples' ? [...] il reste toujours à la même
        // position au morceau suivant") : cf. même correctif dans FullPatternWindow.xaml.cs
        // — changer ItemsSource ne réinitialise pas seul le défilement du ListBox.
        if (instrItems.Count > 0) MainInstrumentsListBox.ScrollIntoView(instrItems[0]);

        RefreshInfoPanelHighlight();
    }

    private void RefreshInfoPanelHighlight()
    {
        if (!_infoPanelOpen) return;
        if (_vm.CurrentOrderIndex >= 0 && _vm.CurrentOrderIndex < MainOrderListBox.Items.Count)
        {
            MainOrderListBox.SelectedIndex = _vm.CurrentOrderIndex;
            MainOrderListBox.ScrollIntoView(MainOrderListBox.SelectedItem);
        }
    }

    /// <summary>Ouvre et joue un fichier tracker.</summary>
    public async Task OpenAsync(string filePath)
        => await _vm.OpenAsync(filePath);

    /// <summary>Arrête la lecture et réinitialise l'affichage (appelé quand on change de release).</summary>
    public void Stop()
    {
        _vm.Stop();
        OscilloscopeView.SampleBuffer = null;
        WaveformOverviewControl.WaveformOverview = null;
        // Fermer le full pattern window si ouvert — libère CompositionTarget.Rendering
        // même si Unloaded ne s'est pas déclenché (ex. fermeture brutale de l'app)
        _fullPatternWindow?.Close();
        _fullPatternWindow = null;
    }

    /// <summary>
    /// Ouvre (ou ramène au premier plan) la fenêtre "Full Pattern View" qui affiche
    /// tous les canaux simultanément sur toute la largeur de l'écran.
    /// Un seul exemplaire à la fois — si la fenêtre est déjà ouverte, elle est
    /// simplement ramenée au premier plan.
    /// </summary>
    private void BtnFullPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_fullPatternWindow is not null && _fullPatternWindow.IsVisible)
        {
            _fullPatternWindow.Activate();
            return;
        }

        // Suspendre le rendu de PatternView pendant que la fenêtre plein écran
        // est active — les deux boucles CompositionTarget.Rendering simultanées
        // se disputaient le thread UI et rendaient les deux non-fluides.
        PatternViewControl.StopRenderLoop();

        _fullPatternWindow = new FullPatternWindow(_vm)
        {
            Owner = Window.GetWindow(this),
        };
        _fullPatternWindow.Closed += (_, _) =>
        {
            _fullPatternWindow = null;
            // Reprendre le rendu de PatternView une fois la fenêtre plein écran fermée
            PatternViewControl.StartRenderLoop();
        };
        _fullPatternWindow.Show();
    }

    private void OnExeMusicWindowReady(object? sender, nint hwnd)
    {
        // Plus utilisé pour le moment — les exe music console n'ont pas de fenêtre à intégrer
    }

    private void DetachExeHost()
    {
        if (_exeHost != null)
        {
            _exeHost.Detach();
            ExeHostContainer.Children.Remove(_exeHost);
            _exeHost = null;
        }
        ExeHostContainer.Visibility = System.Windows.Visibility.Collapsed;
    }
}
