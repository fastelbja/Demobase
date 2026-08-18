using DemoBase.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DemoBase.App.Views.Releases;

public partial class VideoPlayerView : UserControl
{
    private VideoPlayerViewModel? _vm;
    private bool _vlcReady;

    // Conteneur parent du VlcVideoView dans le layout normal
    private Grid? _videoZone;

    public VideoPlayerView()
    {
        InitializeComponent();

        // Drag du thumb
        SeekSlider.AddHandler(
            Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _vm?.BeginSeek()));
        SeekSlider.AddHandler(
            Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => _vm?.EndSeek(SeekSlider.Value)));

        // Clic direct sur la barre
        SeekSlider.PreviewMouseDown += (_, _) => _vm?.BeginSeek();
        SeekSlider.PreviewMouseUp   += (_, e) =>
        {
            var pos     = e.GetPosition(SeekSlider);
            var ratio   = Math.Clamp(pos.X / SeekSlider.ActualWidth, 0.0, 1.0);
            _vm?.EndSeek(ratio * SeekSlider.Maximum);
        };

        VlcVideoView.IsVisibleChanged += OnVlcVisibilityChanged;
        DataContextChanged            += OnDataContextChanged;

        Loaded += (_, _) =>
        {
            // Mémoriser le parent Grid de VlcVideoView pour le reparenting FS
            _videoZone = VlcVideoView.Parent as Grid;
        };
    }

    // ── HWND prêt ────────────────────────────────────────────────────────────
    private void OnVlcVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) return;
        if (!_vlcReady) { _vlcReady = true; ConnectAndPlay(); }
        else if (_vm?.MediaPlayer != null && VlcVideoView.MediaPlayer == null)
            VlcVideoView.MediaPlayer = _vm.MediaPlayer;
    }

    // ── DataContext ───────────────────────────────────────────────────────────
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) { _vm.PropertyChanged -= OnVmPropertyChanged; VlcVideoView.MediaPlayer = null; }
        _vm = DataContext as VideoPlayerViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        if (_vlcReady) ConnectAndPlay();
    }

    private void ConnectAndPlay()
    {
        if (_vm == null || !_vlcReady) return;
        _vm.InitializeVlc();
        VlcVideoView.MediaPlayer = _vm.MediaPlayer;
        _vm.PlayCurrent();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerViewModel.MediaPlayer)
            && _vm?.MediaPlayer != null && _vlcReady && VlcVideoView.MediaPlayer == null)
            Dispatcher.Invoke(() => VlcVideoView.MediaPlayer = _vm.MediaPlayer);
    }

    // ── Plein écran : reparenting du VideoView existant ──────────────────────
    private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _videoZone == null) return;

        // Retirer le VideoView de son parent Grid
        _videoZone.Children.Remove(VlcVideoView);

        var fsWin = new FullscreenVideoWindow(VlcVideoView, _vm, Window.GetWindow(this));

        fsWin.Closed += (_, _) =>
        {
            // Remettre le VideoView dans son parent d'origine
            fsWin.ClearVideoView();
            _videoZone.Children.Add(VlcVideoView);
            Grid.SetColumn(VlcVideoView, 1);
        };

        fsWin.Show();
    }
}

/// <summary>
/// Fenêtre plein écran qui accueille physiquement le VideoView WPF existant.
/// Pas de réassignation de MediaPlayer — le VideoView est simplement déplacé.
/// </summary>
public class FullscreenVideoWindow : Window
{
    private readonly LibVLCSharp.WPF.VideoView _videoView;

    private readonly VideoPlayerViewModel _vm;

    public FullscreenVideoWindow(LibVLCSharp.WPF.VideoView videoView, VideoPlayerViewModel vm, Window? owner)
    {
        _videoView = videoView;
        _vm        = vm;

        WindowStyle   = WindowStyle.None;
        WindowState   = WindowState.Maximized;
        ResizeMode    = ResizeMode.NoResize;
        Background    = System.Windows.Media.Brushes.Black;
        Topmost       = true;
        ShowInTaskbar = false;
        Title         = "DemoBase — Vidéo";
        if (owner != null) Owner = owner;

        // Placer le VideoView existant (avec son MediaPlayer actif) dans cette fenêtre
        Content = _videoView;

        KeyDown          += OnKeyDown;
        MouseDoubleClick += (_, _) => Close();
    }

    // Appelé avant la fermeture pour détacher le VideoView du Content
    // (nécessaire avant de le remettre dans l'arbre parent)
    public void ClearVideoView() => Content = null;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space:
                if (_vm.IsPlaying) _vm.PauseCommand.Execute(null);
                else               _vm.PlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                _vm.EndSeek(Math.Max(0, _vm.PositionSeconds - 5));
                e.Handled = true;
                break;
            case Key.Right:
                _vm.EndSeek(Math.Min(_vm.DurationSeconds, _vm.PositionSeconds + 5));
                e.Handled = true;
                break;
            case Key.M:
                _vm.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
