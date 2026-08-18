using System;
using NAudio.Wave;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Provider audio double-buffer pour le mode Long Play.
    ///
    /// DESIGN :
    ///   - Un seul WaveOutEvent reste ouvert toute la session.
    ///   - Deux slots (A et B) contiennent chacun un IWaveProvider.
    ///   - TrackEnded est déclenché une seule fois quand le slot actif est épuisé.
    ///   - SwapToNext() est appelé explicitement par le caller (dans le thread audio
    ///     via OnDualBufferTrackEnded) pour basculer vers le slot suivant.
    ///   - Read() retourne du silence une fois le slot épuisé, jusqu'au swap.
    ///   - Format : IEEE Float 32-bit stéréo 48000 Hz (libopenmpt natif).
    /// </summary>
    public sealed class DualBufferWaveProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        private volatile IWaveProvider? _slotA;
        private volatile IWaveProvider? _slotB;
        private volatile int            _activeSlot      = 0; // 0=A, 1=B
        private volatile bool           _trackEndedFired = false;

        /// <summary>Déclenché quand le slot actif est épuisé (une seule fois par morceau).</summary>
        public event EventHandler? TrackEnded;

        /// <summary>True si le slot inactif est chargé et prêt.</summary>
        public bool NextReady => _activeSlot == 0 ? _slotB is not null : _slotA is not null;

        /// <summary>Charge le premier morceau dans le slot A et active A.</summary>
        public void LoadFirst(IWaveProvider provider)
        {
            _slotA           = provider;
            _slotB           = null;
            _activeSlot      = 0;
            _trackEndedFired = false;
        }

        /// <summary>Charge le prochain morceau dans le slot inactif.</summary>
        public void LoadNext(IWaveProvider provider)
        {
            if (_activeSlot == 0)
                _slotB = provider;
            else
                _slotA = provider;
        }

        /// <summary>
        /// Bascule vers le slot inactif.
        /// Doit être appelé après avoir reçu TrackEnded et chargé le prochain provider.
        /// Thread-safe : peut être appelé depuis le thread audio.
        /// </summary>
        public bool SwapToNext()
        {
            var next = _activeSlot == 0 ? _slotB : _slotA;
            if (next is null) return false;

            // Libérer le slot qui vient d'être joué
            if (_activeSlot == 0) _slotA = null;
            else                  _slotB = null;

            _activeSlot      = _activeSlot == 0 ? 1 : 0;
            _trackEndedFired = false;
            System.Diagnostics.Debug.WriteLine($"[DB] SwapToNext → slot {_activeSlot}");
            return true;
        }

        /// <summary>Vide les deux slots.</summary>
        public void Clear()
        {
            _slotA           = null;
            _slotB           = null;
            _trackEndedFired = false;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var active = _activeSlot == 0 ? _slotA : _slotB;

            if (active is null)
            {
                // Silence — en attente du prochain LoadNext + SwapToNext
                Array.Clear(buffer, offset, count);
                return count;
            }

            int read = active.Read(buffer, offset, count);

            if (read < count)
                Array.Clear(buffer, offset + read, count - read);

            // Slot épuisé → déclencher TrackEnded une seule fois
            if (read == 0 && !_trackEndedFired)
            {
                _trackEndedFired = true;
                System.Diagnostics.Debug.WriteLine($"[DB] Slot {_activeSlot} ended → TrackEnded");
                TrackEnded?.Invoke(this, EventArgs.Empty);
            }

            return count; // toujours retourner count pour garder WaveOut actif
        }
    }
}
