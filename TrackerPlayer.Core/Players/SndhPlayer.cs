using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Player pour les fichiers SNDH (Atari ST), basé sur SndhPlayer.dll
    /// (P/Invoke vers la lib AtariAudio d'Arnaud Carré — émulation 68000/YM2149
    /// complète). Remplace ZXTunePlayer pour ce format, que ZXTune ne supporte
    /// pas réellement malgré l'extension listée dans ses formats gérés.
    ///
    /// Contrairement à ZXTunePlayer (process externe + fichier WAV intermédiaire),
    /// ce player génère l'audio in-process via SndhStream, sans fichier temporaire
    /// ni process externe.
    /// </summary>
    public sealed class SndhPlayer : ITrackerPlayer
    {
        /// <summary>
        /// Nom de la DLL recherchée, dans le répertoire de l'application
        /// (Externals/ ou racine — voir <see cref="IsAvailable"/>).
        /// </summary>
        public const string DllFileName = "SndhPlayer.dll";

        public static readonly System.Collections.Generic.HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".sndh" };

        /// <summary>Vérifie si SndhPlayer.dll est présente et chargeable.</summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    var local = Path.Combine(AppContext.BaseDirectory, DllFileName);
                    var externals = Path.Combine(AppContext.BaseDirectory, "Externals", DllFileName);
                    if (!File.Exists(local) && !File.Exists(externals)) return false;

                    // Tente un cycle Create/Destroy minimal pour vérifier que la
                    // DLL se charge réellement (bonne architecture x64, dépendances
                    // satisfaites...), pas seulement que le fichier existe.
                    var h = SndhNative.Sndh_Create();
                    if (h == IntPtr.Zero) return false;
                    SndhNative.Sndh_Destroy(h);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        // ── ITrackerPlayer ────────────────────────────────────────────
        public TrackerFormat[] SupportedFormats => [];   // détection par extension
        public event EventHandler<Models.PlaybackState>? StateChanged;
        public event EventHandler?                       PlaybackFinished;
        public Models.PlaybackState CurrentState => _state;
        // 2026-07-30 : SndhStream expose déjà SubsongCount (Sndh_GetSubsongCount)
        // mais rien ne pilote encore la sélection du subsong ici — un seul
        // morceau lu (le défaut de la lib) pour l'instant. Stub cohérent avec
        // les autres players tant que la navigation SNDH n'est pas implémentée.
        public int  SubsongCount        => 1;
        public int  CurrentSubsongIndex => 0;
        public void SelectSubsong(int index) { /* pas encore implémenté pour SNDH */ }
        public float MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = value; if (_waveOut != null) _waveOut.Volume = value; }
        }

        private Models.PlaybackState _state = new();
        private float                _masterVolume = 1.0f;
        private readonly ILogger     _log;
        private WaveOutEvent?        _waveOut;
        private SndhStream?          _stream;
        private CancellationTokenSource? _pollCts;
        private TrackerModule?       _module;

        /// <summary>Buffer circulaire pour l'oscilloscope / pattern viewer.</summary>
        public SampleRingBuffer SampleBuffer { get; } = new SampleRingBuffer(8192);

        // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme
        // d'onde sous l'oscilloscope, remplie progressivement pendant la lecture
        // (Sndh_AudioRender synthétise en temps réel, pas de fichier WAV
        // intermédiaire). Redimensionnée (SetDuration) dans LoadAsync, dès que la
        // durée (réelle ou de repli) est connue.
        public WaveformOverviewBuffer WaveformOverview { get; } = new WaveformOverviewBuffer();

        /// <summary>Durée résolue lors du dernier LoadAsync — réutilisée par Play()
        /// s'il doit recréer le stream (cf. IsReady == false).</summary>
        private double _lastKnownDuration = 300;

        public SndhPlayer(ILogger? logger = null)
        {
            _log = logger ?? NullLogger.Instance;
        }

        public Task LoadAsync(TrackerModule module, CancellationToken ct = default)
        {
            _module = module;
            Stop(); // arrête et libère tout (waveOut + ancien stream)

            _stream = new SndhStream(module.FilePath, SampleBuffer, _log, WaveformOverview);

            // Beaucoup de fichiers SNDH n'ont pas de tag TIME/FRMS renseignant
            // la durée (cf. DurationSeconds = 0 dans ce cas) : on retombe alors
            // sur la durée déjà connue du module, sinon une limite raisonnable
            // par défaut — même fallback que ZXTunePlayer pour rester cohérent
            // côté UI (barre de progression, etc.).
            double duration = _stream.DurationSeconds > 0
                ? _stream.DurationSeconds
                : (module.DurationSeconds > 0 ? module.DurationSeconds : 300);
            _lastKnownDuration = duration;
            WaveformOverview.SetDuration(duration, 44100);

            _state = new Models.PlaybackState
            {
                DurationSeconds = duration,
                CurrentBpm      = 125
            };

            // Propage la durée résolue (vraie durée si connue, sinon le
            // fallback de 300s) sur le module : le ViewModel relit
            // module.DurationSeconds et applique SON PROPRE fallback (1s) si
            // cette valeur est à 0, donc il faut que module.DurationSeconds
            // reflète déjà la décision prise ici, pas seulement la valeur
            // locale "duration" utilisée pour _state.
            module.DurationSeconds = duration;

            if (!string.IsNullOrWhiteSpace(_stream.DetectedTitle))
                module.Title = _stream.DetectedTitle;
            if (!string.IsNullOrWhiteSpace(_stream.DetectedAuthor))
                module.Author = _stream.DetectedAuthor;
            module.FormatName = "SNDH (Atari ST)";

            return Task.CompletedTask;
        }

        public void Play()
        {
            if (_module is null) return;

            // Ne pas appeler Stop() ici — ça disposerait le stream créé dans LoadAsync
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;

            if (_stream is null || !_stream.IsReady)
            {
                _stream = new SndhStream(_module.FilePath, SampleBuffer, _log, WaveformOverview);
                WaveformOverview.SetDuration(_lastKnownDuration, 44100);
            }
            else
            {
                _stream.SeekToStart();
                WaveformOverview.Reset();
            }

            _waveOut = new WaveOutEvent { DesiredLatency = 200 };
            _waveOut.Volume = _masterVolume;
            _waveOut.Init(_stream);
            _waveOut.PlaybackStopped += (_, _) =>
            {
                _state.IsPlaying = false;
                PlaybackFinished?.Invoke(this, EventArgs.Empty);
            };
            _waveOut.Play();
            _state.IsPlaying = true;
            _state.IsPaused  = false;

            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }

        public void Pause()
        {
            if (_waveOut?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                _waveOut.Pause();
                _state.IsPaused  = true;
                _state.IsPlaying = false;
                NotifyState();
            }
        }

        public void Stop()
        {
            _pollCts?.Cancel();
            _waveOut?.Stop();
            _waveOut?.Dispose(); _waveOut = null;
            _stream?.Dispose();  _stream  = null;
            _state.IsPlaying = _state.IsPaused = false;
        }

        public void SeekToOrder(int orderIndex) { /* pas de seek pour le moment */ }

        private async Task PollAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _state.IsPlaying)
            {
                _state.PositionSeconds = _stream?.PositionSeconds ?? 0;

                // Beaucoup de SNDH n'ont pas de durée connue (DurationSeconds=0
                // résolu en fallback 300s côté LoadAsync) : si la position
                // dépasse la durée "fallback" sans tag réel, on laisse jouer
                // plutôt que de couper arbitrairement — le fichier boucle
                // naturellement côté lib (m_loopCount) sans erreur.
                NotifyState();
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        private void NotifyState()
        {
            var copy = new Models.PlaybackState
            {
                IsPlaying       = _state.IsPlaying,
                IsPaused        = _state.IsPaused,
                PositionSeconds = _state.PositionSeconds,
                DurationSeconds = _state.DurationSeconds,
                CurrentBpm      = _state.CurrentBpm,
                CurrentRow      = _state.CurrentRow,
            };
            StateChanged?.Invoke(this, copy);
        }

        public void Dispose() => Stop();
    }
}
