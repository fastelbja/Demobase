using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace TrackerPlayer.Core.Players
{
    // ════════════════════════════════════════════════════════════════════════
    // Pont natif zxtune.dll — P/Invoke direct, en remplacement du process
    // externe zxtune123.exe + génération de fichier WAV temporaire utilisé
    // jusqu'ici par ZXTunePlayer (cf. commentaire de classe sur ZXTunePlayer
    // dans ExternalPlayers.cs).
    //
    // 2026-08-06, retour utilisateur : "j'ai réussi à compiler une DLL pour
    // utiliser zxtune sans externals. ça pourra aussi eviter de passer par la
    // génération d'un wav et la detection des subsongs est instantanée. peux
    // tu regarder ce projet [ZxTuneWpfDemo.zip] et t'en inspirer pour intégrer
    // la DLL en lieu et place de zxtune123". Ce fichier reprend quasi
    // intégralement l'architecture du projet de démo fourni (NativeMethods.cs
    // / ZxTunePlayer.cs / ZxTuneContainer.cs / ZxTuneWaveProvider.cs), avec
    // deux adaptations pour s'intégrer à TrackerPlayer.Core :
    //   1. Renommage des classes haut niveau (Zx* → ZxNative*) pour éviter
    //      toute confusion avec ZXTunePlayer/ZXTuneDecoder (ITrackerPlayer/
    //      ITrackerDecoder existants, casse différente mais noms très proches).
    //   2. ZxTuneWaveProvider alimente aussi SampleRingBuffer (oscilloscope),
    //      ce que le projet de démo (sans UI oscilloscope) n'avait pas besoin
    //      de faire.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handle natif "ZxHandle" renvoyé par Zx_Open/Zx_OpenSubsong. Garantit la
    /// libération via Zx_Close même en cas d'exception ou de collecte GC.
    /// </summary>
    internal sealed class ZxNativeHandle : SafeHandle
    {
        public ZxNativeHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            ZxTuneNativeInterop.Zx_Close(handle);
            return true;
        }
    }

    /// <summary>
    /// Handle natif pour un conteneur ouvert via Zx_OpenContainer (liste des
    /// pistes/"subsongs" découvertes dans un fichier). Libéré via
    /// Zx_CloseContainer.
    /// </summary>
    internal sealed class ZxContainerNativeHandle : SafeHandle
    {
        public ZxContainerNativeHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            ZxTuneNativeInterop.Zx_CloseContainer(handle);
            return true;
        }
    }

    /// <summary>Déclarations DllImport vers zxtune.dll (module simple + conteneur/subsongs).</summary>
    internal static class ZxTuneNativeInterop
    {
        // Nom de la DLL native (sans extension). Doit être déployée en x64,
        // dans le répertoire de l'application ou dans Externals/ (ajouté au
        // PATH au démarrage, cf. App.xaml.cs/ConfigureExternalPaths) — même
        // schéma de résolution que libopenmpt.dll.
        private const string DllName = "zxtune";

        /// <summary>
        /// Vrai si zxtune.dll est chargeable (présente, bonne architecture,
        /// exports attendus). Ne nécessite pas de fichier module valide,
        /// contrairement à un appel réel — utilisé par ZXTunePlayer.IsAvailable
        /// pour décider si ce format doit basculer sur ce lecteur ou sur le
        /// repli libopenmpt.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    return NativeLibrary.TryLoad(DllName, typeof(ZxTuneNativeInterop).Assembly, null, out _);
                }
                catch
                {
                    return false;
                }
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZxNativeHandle Zx_Open(byte[] data, int dataSize, string subpath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_Close(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_Render(ZxNativeHandle handle, short[] buffer, int sampleCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_Seek(ZxNativeHandle handle, double seconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_Reset(ZxNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetSampleRate(ZxNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetChannels(ZxNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double Zx_GetDurationSeconds(ZxNativeHandle handle);

        // Attention : name est un const char* côté natif (ASCII, ex. "Title"),
        // tandis que buffer est un uint16_t* (UTF-16) — deux marshalings
        // différents dans la même signature, d'où le MarshalAs explicite.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int Zx_GetProperty(
            ZxNativeHandle handle,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            char[] buffer,
            int bufferChars);

        // Position réelle / bouclage / volume : lus/écrits directement depuis
        // Module::State côté natif (plus fiable qu'un calcul côté C# à partir
        // des trames rendues, qui ne gérerait pas le bouclage).
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double Zx_GetPositionSeconds(ZxNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetLoopCount(ZxNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_SetGain(ZxNativeHandle handle, double gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_SetLooped(ZxNativeHandle handle, int looped);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_SetLoopLimit(ZxNativeHandle handle, int limit);

        // Métadonnées tracker (patterns/lignes) — retournent 0 (aucun
        // out-param écrit) pour les formats qui n'exposent pas cette
        // structure (VGM, GME, SID, PSF...) ; ce n'est pas une erreur.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetTrackInfo(
            ZxNativeHandle handle, out int channelsCount, out int positionsCount, out int loopPosition);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetTrackState(
            ZxNativeHandle handle, out int position, out int pattern, out int line, out int tempo,
            out int activeChannels);

        // API "subsongs" (conteneurs multi-pistes : .ay, .sap, .hes, .emul...)
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZxContainerNativeHandle Zx_OpenContainer(byte[] data, int dataSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Zx_CloseContainer(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Zx_GetSubsongCount(ZxContainerNativeHandle handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double Zx_GetSubsongDurationSeconds(ZxContainerNativeHandle handle, int index);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int Zx_GetSubsongProperty(
            ZxContainerNativeHandle handle,
            int index,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            char[] buffer,
            int bufferChars);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ZxNativeHandle Zx_OpenSubsong(ZxContainerNativeHandle handle, int index);
    }

    /// <summary>Structure interne (patterns/lignes) d'un morceau — formats "tracker" uniquement.</summary>
    internal readonly struct ZxNativeTrackInfo
    {
        public ZxNativeTrackInfo(int channelsCount, int positionsCount, int loopPosition)
        {
            ChannelsCount   = channelsCount;
            PositionsCount  = positionsCount;
            LoopPosition    = loopPosition;
        }

        public int ChannelsCount  { get; }
        public int PositionsCount { get; }
        public int LoopPosition   { get; }
    }

    /// <summary>État tracker courant (position/pattern/ligne/tempo/canaux actifs).</summary>
    internal readonly struct ZxNativeTrackState
    {
        public ZxNativeTrackState(int position, int pattern, int line, int tempo, int activeChannels)
        {
            Position       = position;
            Pattern        = pattern;
            Line           = line;
            Tempo          = tempo;
            ActiveChannels = activeChannels;
        }

        public int Position       { get; }
        public int Pattern        { get; }
        public int Line           { get; }
        public int Tempo          { get; }
        public int ActiveChannels { get; }
    }

    /// <summary>
    /// Métadonnées d'une piste ("subsong") découverte par
    /// <see cref="ZxNativeContainer"/>, sans avoir besoin de l'ouvrir pour la
    /// jouer — la découverte (Service::DetectModules côté natif) est quasi
    /// instantanée, contrairement au sondage "?#N" un par un via zxtune123.exe
    /// utilisé auparavant (un rendu WAV complet par subsong sondé).
    /// </summary>
    internal sealed class ZxNativeSubsongInfo
    {
        public int      Index    { get; init; }
        public string?  Title    { get; init; }
        public string?  Author   { get; init; }
        public string?  Type     { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Wrapper haut niveau autour du cœur audio zxtune. Représente un module
    /// (un "subsong" précis, déjà résolu) ouvert et prêt à être rendu
    /// échantillon par échantillon.
    /// </summary>
    internal sealed class ZxNativeSubsongPlayer : IDisposable
    {
        private readonly ZxNativeHandle _handle;
        private bool _disposed;

        private ZxNativeSubsongPlayer(ZxNativeHandle handle)
        {
            _handle    = handle;
            SampleRate = ZxTuneNativeInterop.Zx_GetSampleRate(handle);
            Channels   = ZxTuneNativeInterop.Zx_GetChannels(handle);
            Duration   = TimeSpan.FromSeconds(ZxTuneNativeInterop.Zx_GetDurationSeconds(handle));

            if (ZxTuneNativeInterop.Zx_GetTrackInfo(handle, out int channelsCount, out int positionsCount,
                    out int loopPosition) != 0)
            {
                TrackInfo = new ZxNativeTrackInfo(channelsCount, positionsCount, loopPosition);
            }
        }

        /// <summary>Construit un player à partir d'un handle natif déjà ouvert (Zx_Open/Zx_OpenSubsong).</summary>
        internal static ZxNativeSubsongPlayer FromNativeHandle(ZxNativeHandle handle)
        {
            if (handle == null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new InvalidOperationException(
                    "zxtune n'a pas reconnu ce fichier (format non supporté ou données invalides).");
            }
            return new ZxNativeSubsongPlayer(handle);
        }

        public int      SampleRate { get; }
        public int      Channels   { get; }
        public TimeSpan Duration   { get; }

        /// <summary>Position de lecture courante (remise à 0 à chaque bouclage).</summary>
        public TimeSpan Position
        {
            get
            {
                ThrowIfDisposed();
                return TimeSpan.FromSeconds(ZxTuneNativeInterop.Zx_GetPositionSeconds(_handle));
            }
        }

        public void SetGain(double gain)
        {
            ThrowIfDisposed();
            ZxTuneNativeInterop.Zx_SetGain(_handle, gain);
        }

        /// <summary>
        /// Active/désactive le bouclage infini par défaut de zxtune.
        /// Désactivé systématiquement par ZXTunePlayer (cf. Play()) : DemoBase
        /// gère lui-même l'avance de playlist / fin de lecture (comme avant
        /// avec zxtune123.exe, qui lui ne bouclait jamais).
        /// </summary>
        public void SetLooped(bool looped)
        {
            ThrowIfDisposed();
            ZxTuneNativeInterop.Zx_SetLooped(_handle, looped ? 1 : 0);
        }

        public ZxNativeTrackInfo? TrackInfo { get; }

        public ZxNativeTrackState? GetTrackState()
        {
            ThrowIfDisposed();
            if (ZxTuneNativeInterop.Zx_GetTrackState(_handle, out int position, out int pattern, out int line,
                    out int tempo, out int activeChannels) == 0)
            {
                return null;
            }
            return new ZxNativeTrackState(position, pattern, line, tempo, activeChannels);
        }

        public string GetProperty(string name)
        {
            ThrowIfDisposed();
            var buffer = new char[512];
            int needed = ZxTuneNativeInterop.Zx_GetProperty(_handle, name, buffer, buffer.Length);
            if (needed <= 0) return string.Empty;

            if (needed > buffer.Length)
            {
                buffer = new char[needed];
                needed = ZxTuneNativeInterop.Zx_GetProperty(_handle, name, buffer, buffer.Length);
            }
            return new string(buffer, 0, Math.Max(0, needed));
        }

        /// <summary>
        /// Rend jusqu'à <paramref name="sampleFrames"/> trames (Channels short
        /// par trame, entrelacés) dans <paramref name="buffer"/>.
        /// </summary>
        /// <returns>Nombre de trames effectivement rendues ; 0 = fin du morceau.</returns>
        public int Render(short[] buffer, int sampleFrames)
        {
            ThrowIfDisposed();
            return ZxTuneNativeInterop.Zx_Render(_handle, buffer, sampleFrames);
        }

        public void Seek(TimeSpan position)
        {
            ThrowIfDisposed();
            ZxTuneNativeInterop.Zx_Seek(_handle, position.TotalSeconds);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZxNativeSubsongPlayer));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }

    /// <summary>
    /// Représente un fichier ouvert au niveau "conteneur" : la liste des
    /// pistes jouables qu'il contient, découvertes automatiquement par zxtune
    /// (Service::DetectModules côté natif) — pour un fichier "simple",
    /// <see cref="Count"/> vaut 1, ce chemin est donc utilisé
    /// systématiquement à l'ouverture d'un fichier par ZXTunePlayer.
    /// </summary>
    internal sealed class ZxNativeContainer : IDisposable
    {
        private readonly ZxContainerNativeHandle _handle;
        private bool _disposed;

        private ZxNativeContainer(ZxContainerNativeHandle handle)
        {
            _handle = handle;
            Count   = ZxTuneNativeInterop.Zx_GetSubsongCount(handle);
        }

        /// <summary>
        /// Analyse le contenu binaire d'un fichier et découvre les pistes
        /// jouables qu'il contient. Retourne null si zxtune n'y reconnaît
        /// aucun format supporté.
        /// </summary>
        public static ZxNativeContainer? Open(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var handle = ZxTuneNativeInterop.Zx_OpenContainer(data, data.Length);
            if (handle == null || handle.IsInvalid)
            {
                handle?.Dispose();
                return null;
            }
            return new ZxNativeContainer(handle);
        }

        /// <summary>Nombre de pistes jouables découvertes (au moins 1 si Open n'a pas retourné null).</summary>
        public int Count { get; }

        public string GetProperty(int index, string name)
        {
            ThrowIfDisposed();
            CheckIndex(index);

            var buffer = new char[512];
            int needed = ZxTuneNativeInterop.Zx_GetSubsongProperty(_handle, index, name, buffer, buffer.Length);
            if (needed <= 0) return string.Empty;

            if (needed > buffer.Length)
            {
                buffer = new char[needed];
                needed = ZxTuneNativeInterop.Zx_GetSubsongProperty(_handle, index, name, buffer, buffer.Length);
            }
            return new string(buffer, 0, Math.Max(0, needed));
        }

        public TimeSpan GetDuration(int index)
        {
            ThrowIfDisposed();
            CheckIndex(index);
            return TimeSpan.FromSeconds(ZxTuneNativeInterop.Zx_GetSubsongDurationSeconds(_handle, index));
        }

        /// <summary>Métadonnées complètes d'une piste, sans l'ouvrir pour la jouer.</summary>
        public ZxNativeSubsongInfo GetInfo(int index)
        {
            ThrowIfDisposed();
            CheckIndex(index);
            return new ZxNativeSubsongInfo
            {
                Index    = index,
                Title    = GetProperty(index, "Title"),
                Author   = GetProperty(index, "Author"),
                Type     = GetProperty(index, "Type"),
                Duration = GetDuration(index),
            };
        }

        /// <summary>Ouvre la piste à l'index donné, prête pour Render/Seek/...</summary>
        public ZxNativeSubsongPlayer OpenSubsong(int index)
        {
            ThrowIfDisposed();
            CheckIndex(index);
            var handle = ZxTuneNativeInterop.Zx_OpenSubsong(_handle, index);
            return ZxNativeSubsongPlayer.FromNativeHandle(handle);
        }

        private void CheckIndex(int index)
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index de piste invalide : {index} (0..{Count - 1}).");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZxNativeContainer));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }

    /// <summary>
    /// Adapte un <see cref="ZxNativeSubsongPlayer"/> en source NAudio (PCM 16
    /// bits, entrelacé selon Channels). Alimente également
    /// <paramref name="sampleBuffer"/> (SampleRingBuffer) à chaque Read() pour
    /// l'affichage oscilloscope — même rôle que ZXTuneStream.Read() côté
    /// implémentation process-based précédente, mais sans le hack de fondu de
    /// fin (FadeOutFrames) : celui-ci compensait une coupure sèche propre au
    /// fichier WAV généré par zxtune123.exe (arrêt pile à Length(+Fade) sans
    /// jamais retomber sur un passage à zéro) ; Zx_Render retourne 0 à la fin
    /// naturelle du rendu natif, sans ce problème.
    /// </summary>
    internal sealed class ZxTuneWaveProvider : IWaveProvider
    {
        private readonly ZxNativeSubsongPlayer   _player;
        private readonly SampleRingBuffer        _sampleBuffer;
        private readonly WaveformOverviewBuffer? _waveformOverview;
        private short[]?                         _renderBuffer;

        public ZxTuneWaveProvider(ZxNativeSubsongPlayer player, SampleRingBuffer sampleBuffer,
            WaveformOverviewBuffer? waveformOverview = null)
        {
            _player           = player ?? throw new ArgumentNullException(nameof(player));
            _sampleBuffer     = sampleBuffer ?? throw new ArgumentNullException(nameof(sampleBuffer));
            _waveformOverview = waveformOverview;
            WaveFormat = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm,
                player.SampleRate,
                player.Channels,
                player.SampleRate * player.Channels * 2, // avgBytesPerSec (16 bits = 2 octets)
                player.Channels * 2,                     // blockAlign
                16);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>
        /// 2026-08-06, retour utilisateur ("j'ai l'impression que zxtune n'est jamais
        /// testé pour les formats inconnus mais uniquement uade") : total cumulé des
        /// trames RÉELLEMENT rendues par ce provider depuis sa création — même
        /// principe que UadePlayer._bytesDecoded (ExternalPlayers.cs, Read()) :
        /// permet à ZXTunePlayer de détecter un "faux positif" d'ouverture (le
        /// conteneur natif a reconnu le fichier et annoncé au moins un subsong, mais
        /// le rendu de CE subsong n'a produit AUCUNE trame audio) une fois la lecture
        /// terminée (cf. ZXTunePlayer.OnWaveOutStopped).
        /// </summary>
        public long FramesRendered { get; private set; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int channels = Math.Max(1, WaveFormat.Channels);
            int bytesPerFrame = channels * 2;
            int framesRequested = count / bytesPerFrame;
            if (framesRequested <= 0) return 0;

            int shortsNeeded = framesRequested * channels;
            if (_renderBuffer == null || _renderBuffer.Length < shortsNeeded)
                _renderBuffer = new short[shortsNeeded];

            int framesRendered = _player.Render(_renderBuffer, framesRequested);
            if (framesRendered <= 0) return 0; // fin du morceau : NAudio arrêtera la lecture

            int bytesToCopy = framesRendered * bytesPerFrame;
            Buffer.BlockCopy(_renderBuffer, 0, buffer, offset, bytesToCopy);

            // Position AVANT ce bloc — FramesRendered n'est incrémenté que plus bas,
            // sert de repère pour ranger ces trames dans le bon bucket de la vue
            // d'ensemble (WaveformOverviewBuffer).
            WriteToSampleBuffer(_renderBuffer, framesRendered, channels, FramesRendered);
            FramesRendered += framesRendered;

            return bytesToCopy;
        }

        /// <summary>
        /// Convertit les trames rendues (short entrelacés, 1 ou 2+ canaux) en
        /// paires L/R float pour l'oscilloscope. Mono → dupliqué sur les deux
        /// canaux ; 2+ canaux → les deux premiers sont utilisés comme L/R
        /// (les formats ZXTune ne dépassent pas la stéréo en pratique).
        /// </summary>
        private void WriteToSampleBuffer(short[] rendered, int frames, int channels, long framePosStart)
        {
            var left  = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
            var right = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    short l = rendered[i * channels];
                    short r = channels >= 2 ? rendered[i * channels + 1] : l;
                    left[i]  = l / 32768f;
                    right[i] = r / 32768f;
                }
                _sampleBuffer.Write(left, right, frames);
                _waveformOverview?.WriteAt(framePosStart, left, right, frames);
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(left);
                System.Buffers.ArrayPool<float>.Shared.Return(right);
            }
        }
    }
}
