using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Flux audio pour les fichiers SNDH (Atari ST), généré à la volée via
    /// SndhPlayer.dll (P/Invoke vers la lib AtariAudio d'Arnaud Carré).
    ///
    /// Contrairement à ZXTuneStream (qui convertit tout le morceau en WAV via
    /// un process externe avant de le lire depuis disque), ce flux appelle
    /// Sndh_AudioRender directement à chaque Read() : pas de fichier
    /// intermédiaire, pas de process externe, rendu mono converti en stéréo
    /// pour rester compatible avec le pipeline NAudio existant (WaveFormat
    /// 44100/16/stereo, comme ZXTuneStream).
    /// </summary>
    internal sealed class SndhStream : IWaveProvider, IDisposable
    {
        private const uint SampleRate = 44100;

        private readonly SampleRingBuffer         _sampleBuffer;
        private readonly WaveformOverviewBuffer?  _waveformOverview;
        private readonly ILogger                  _log;
        private IntPtr  _handle;
        private bool    _ready;
        private double  _positionSeconds;

        // Buffer mono réutilisable pour éviter une allocation à chaque Read().
        private short[] _monoScratch = Array.Empty<short>();

        public string? DetectedTitle  { get; private set; }
        public string? DetectedAuthor { get; private set; }
        public string? DetectedYear   { get; private set; }
        public int      SubsongCount   { get; private set; }
        public int      DefaultSubsong { get; private set; }

        public bool   IsReady         => _ready;
        public double PositionSeconds => _positionSeconds;

        /// <summary>
        /// Durée annoncée par les tags SNDH (TIME/FRMS), si présents. 0 si le
        /// fichier ne les renseigne pas (cas fréquent — beaucoup de SNDH n'ont
        /// pas de balise de durée ; il faut alors une limite raisonnable côté
        /// appelant plutôt que de boucler indéfiniment).
        public double DurationSeconds { get; private set; }

        // Sortie : stéréo 16-bit 44100Hz, cohérent avec ZXTuneStream / le reste
        // du pipeline NAudio (les canaux gauche/droit sont identiques puisque
        // la lib ne produit que du mono).
        public WaveFormat WaveFormat { get; } = new WaveFormat((int)SampleRate, 16, 2);

        public SndhStream(string filePath, SampleRingBuffer sampleBuffer, ILogger? log = null,
            WaveformOverviewBuffer? waveformOverview = null)
        {
            _sampleBuffer     = sampleBuffer;
            _waveformOverview = waveformOverview;
            _log              = log ?? NullLogger.Instance;

            try
            {
                byte[] rawData = File.ReadAllBytes(filePath);

                _handle = SndhNative.Sndh_Create();
                if (_handle == IntPtr.Zero)
                {
                    _log.LogWarning("SndhStream: Sndh_Create a retourné un handle nul.");
                    return;
                }

                int loadOk = SndhNative.Sndh_Load(_handle, rawData, rawData.Length, SampleRate);
                if (loadOk == 0)
                {
                    _log.LogWarning("SndhStream: Sndh_Load a échoué pour '{File}'.", filePath);
                    return;
                }

                SubsongCount   = SndhNative.Sndh_GetSubsongCount(_handle);
                DefaultSubsong = SndhNative.Sndh_GetDefaultSubsong(_handle);

                if (SndhNative.Sndh_GetSubsongInfo(_handle, DefaultSubsong, out var info) != 0)
                {
                    DetectedTitle  = info.musicName;
                    DetectedAuthor = info.musicAuthor;
                    DetectedYear   = info.year;

                    // playerTickCount vaut souvent 0 (beaucoup de fichiers SNDH
                    // n'ont pas de tag TIME/FRMS) : dans ce cas DurationSeconds
                    // reste à 0, et l'appelant doit appliquer sa propre limite
                    // (cf. ZXTuneStream.DurationSeconds qui retombe sur un
                    // fallback de 300s côté ZXTunePlayer.LoadAsync).
                    if (info.playerTickRate > 0 && info.playerTickCount > 0)
                        DurationSeconds = (double)info.playerTickCount / info.playerTickRate;

                    _log.LogInformation(
                        "SndhStream: chargé '{Title}' / '{Author}' — {N} sous-morceau(x), défaut={D}, durée={Dur}s",
                        DetectedTitle, DetectedAuthor, SubsongCount, DefaultSubsong, DurationSeconds);
                }

                int initOk = SndhNative.Sndh_InitSubSong(_handle, DefaultSubsong);
                if (initOk == 0)
                {
                    _log.LogWarning("SndhStream: Sndh_InitSubSong a échoué pour le sous-morceau {Id}.", DefaultSubsong);
                    return;
                }

                _ready = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SndhStream: erreur d'initialisation pour '{File}'", filePath);
            }
        }

        /// <summary>Remet la lecture au début du sous-morceau courant.</summary>
        public void SeekToStart()
        {
            if (!_ready) return;
            SndhNative.Sndh_InitSubSong(_handle, DefaultSubsong);
            _positionSeconds = 0;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_ready) return count; // silence si non chargé, comme ZXTuneStream

            // count est en octets, format de sortie stéréo 16-bit → 4 octets/frame.
            int framesRequested = count / 4;
            if (framesRequested <= 0) return 0;

            if (_monoScratch.Length < framesRequested)
                _monoScratch = new short[framesRequested];

            int rendered;
            try
            {
                // Sndh_AudioRender retourne le compteur de boucles musicales
                // (m_loopCount côté lib), PAS le nombre d'échantillons rendus —
                // le buffer est toujours rempli intégralement avec "count"
                // échantillons par construction de la boucle interne.
                SndhNative.Sndh_AudioRender(_handle, _monoScratch, framesRequested, IntPtr.Zero);
                rendered = framesRequested;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SndhStream.Read: erreur Sndh_AudioRender");
                return 0;
            }

            var left  = new float[rendered];
            var right = new float[rendered];

            for (int i = 0; i < rendered; i++)
            {
                short s = _monoScratch[i];
                int byteIdx = offset + i * 4;
                buffer[byteIdx]     = (byte)(s & 0xFF);
                buffer[byteIdx + 1] = (byte)((s >> 8) & 0xFF);
                buffer[byteIdx + 2] = (byte)(s & 0xFF);
                buffer[byteIdx + 3] = (byte)((s >> 8) & 0xFF);

                float f = s / 32768f;
                left[i]  = f;
                right[i] = f;
            }

            _sampleBuffer.Write(left, right, rendered);
            // Position AVANT ce bloc — _positionSeconds n'est incrémenté qu'après,
            // sert de repère pour ranger ces trames dans le bon bucket de la vue
            // d'ensemble (WaveformOverviewBuffer).
            _waveformOverview?.WriteAt((long)(_positionSeconds * SampleRate), left, right, rendered);
            _positionSeconds += (double)rendered / SampleRate;

            return rendered * 4;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                SndhNative.Sndh_Unload(_handle);
                SndhNative.Sndh_Destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
