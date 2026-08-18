using System;
using System.Threading;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Buffer circulaire thread-safe pour les samples PCM stéréo float.
    /// Écrit depuis le thread audio NAudio, lu depuis le thread UI (CompositionTarget).
    /// Conçu pour l'oscilloscope : on veut toujours les N derniers samples.
    /// </summary>
    public sealed class SampleRingBuffer
    {
        private readonly float[] _left;
        private readonly float[] _right;
        private readonly int     _capacity;
        private int  _writePos   = 0;
        private long _lastWriteMs = 0; // timestamp du dernier Write() en ms (Environment.TickCount64)

        /// <summary>Délai max sans écriture avant que ReadLast() retourne des zéros (ms).</summary>
        private const int SilenceThresholdMs = 200;

        public int Capacity => _capacity;
        public int WritePos => _writePos;

        public SampleRingBuffer(int capacity = 4096)
        {
            _capacity = capacity;
            _left     = new float[capacity];
            _right    = new float[capacity];
        }

        /// <summary>
        /// Écrit des samples dans le buffer depuis le thread audio.
        /// Thread-safe via Interlocked sur _writePos.
        /// </summary>
        public void Write(float[] left, float[] right, int count)
        {
            int pos = _writePos;
            for (int i = 0; i < count; i++)
            {
                int idx = (pos + i) % _capacity;
                _left[idx]  = left[i];
                _right[idx] = right[i];
            }
            Interlocked.Exchange(ref _writePos, (pos + count) % _capacity);
            Interlocked.Exchange(ref _lastWriteMs, Environment.TickCount64);
        }

        /// <summary>
        /// Remet le buffer à zéro (appelé quand la lecture s'arrête).
        /// Garantit que l'oscilloscope affiche une ligne plate après Stop().
        /// </summary>
        public void Clear()
        {
            Array.Clear(_left,  0, _capacity);
            Array.Clear(_right, 0, _capacity);
            Interlocked.Exchange(ref _lastWriteMs, 0); // force silence immédiat
        }

        /// <summary>
        /// Lit les N derniers samples dans des tableaux de destination.
        /// Retourne des zéros si aucun Write() n'a eu lieu depuis SilenceThresholdMs.
        /// Peut être appelé depuis n'importe quel thread.
        /// </summary>
        public void ReadLast(int count, float[] outLeft, float[] outRight)
        {
            // Si le thread audio n'écrit plus, afficher une ligne plate
            long last = Volatile.Read(ref _lastWriteMs);
            if (last == 0 || Environment.TickCount64 - last > SilenceThresholdMs)
            {
                Array.Clear(outLeft,  0, count);
                Array.Clear(outRight, 0, count);
                return;
            }

            int pos = Volatile.Read(ref _writePos);
            for (int i = 0; i < count; i++)
            {
                int idx = (pos - count + i + _capacity * 2) % _capacity;
                outLeft[i]  = _left[idx];
                outRight[i] = _right[idx];
            }
        }
    }
}
