using System;
using NAudio.Wave;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// IWaveProvider wrapper qui permet de swapper la source audio à la volée
    /// sans réinitialiser NAudio (pas de WaveOutEvent.Stop/Init/Play).
    ///
    /// On crée un seul WaveOutEvent pour toute la session Long Play,
    /// et on change juste la source interne au moment du swap.
    ///
    /// Thread-safety : _source est lue/écrite atomiquement via volatile.
    /// </summary>
    public sealed class SwappableWaveProvider : IWaveProvider
    {
        // Format fixe pour toute la session — DOIT correspondre exactement au format
        // produit par les sources swappées, cf. OpenMptStream (NativeTrackerPlayer.cs) :
        // IEEE Float 32-bit, 48000 Hz, stéréo. Read() ne fait aucun resampling/
        // conversion, donc un mauvais format ici produirait un son accéléré/ralenti
        // ou déformé (bug initialement présent : 44100 Hz 16-bit PCM, incompatible
        // avec la sortie réelle de libopenmpt — jamais détecté car cette classe
        // n'était encore branchée nulle part).
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        private volatile IWaveProvider? _source;

        /// <summary>Source actuelle. Peut être changée à tout moment depuis n'importe quel thread.</summary>
        public IWaveProvider? Source
        {
            get => _source;
            set => _source = value;
        }

        /// <summary>
        /// True si la source a signalé EOF (Read a retourné 0).
        /// Remis à false lors du swap.
        /// </summary>
        public bool EndOfSource { get; private set; }

        /// <summary>Callback déclenché quand la source retourne 0 octets (fin de morceau).</summary>
        public event EventHandler? SourceEnded;

        public int Read(byte[] buffer, int offset, int count)
        {
            var src = _source;
            if (src is null)
            {
                // Silence si pas de source
                Array.Clear(buffer, offset, count);
                return count;
            }

            int read = src.Read(buffer, offset, count);

            if (read == 0 && !EndOfSource)
            {
                EndOfSource = true;
                // L'abonné (SoundtrackPlayerViewModel.OnSharedSourceEnded) peut swapper _source
                // ICI, DE FAÇON SYNCHRONE, pendant cet appel — c'est exactement le but : on est
                // sur le thread audio NAudio, au sample près de la fin réelle de la piste. Si un
                // swap a bien eu lieu (Source a changé), on retente IMMÉDIATEMENT la lecture sur
                // la nouvelle source AU LIEU de renvoyer du silence pour ce buffer — c'est ce qui
                // rend la transition gapless sans avoir à deviner une marge d'anticipation avant
                // la fin (ancienne approche, abandonnée le 2026-07-24 : cf. le commentaire sur
                // OnSharedSourceEnded côté DemoBase — trop tôt tronquait la piste courante, trop
                // tard laissait un trou). Un seul niveau de retry (pas de boucle) : si la
                // nouvelle source est elle-même vide, on renvoie du silence plutôt que de risquer
                // une récursion en cas de chaîne de sources vides.
                SourceEnded?.Invoke(this, EventArgs.Empty);

                var newSrc = _source;
                if (!ReferenceEquals(newSrc, src) && newSrc != null)
                {
                    EndOfSource = false;
                    read = newSrc.Read(buffer, offset, count);
                }
            }

            if (read < count)
            {
                // Remplit le reste avec du silence (source toujours épuisée, ou nouvelle
                // source elle-même trop courte pour ce buffer).
                Array.Clear(buffer, offset + read, count - read);
            }

            return count; // toujours retourner count pour garder NAudio en marche
        }

        /// <summary>
        /// Swapper la source à chaud. Remet EndOfSource à false.
        /// Doit être appelé depuis le thread UI ou un thread synchronisé.
        /// </summary>
        public void Swap(IWaveProvider newSource)
        {
            EndOfSource = false;
            _source     = newSource;
        }
    }
}
