using System;
using System.Threading;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// 2026-08-07, demande utilisateur : "dans la vue oscilloscope, j'aimerais bien
    /// rajouter en bas une visualisation complète du .wav joué [...] pour certains
    /// formats le wav est généré à la volée il pourra se mettre à jour au fur et à
    /// mesure de la lecture. pour les autres il pourra générer la visu dans un
    /// thread séparé". Confirmé ensuite : "pas necessaire pour les musiques
    /// executables, mais ok pour le reste".
    ///
    /// Buffer d'enveloppe min/max sur un nombre FIXE de "buckets" couvrant toute la
    /// durée du morceau (contrairement à SampleRingBuffer qui ne garde que les N
    /// derniers samples pour l'oscilloscope temps réel). Deux modes d'alimentation :
    ///   - Remplissage PROGRESSIF pendant la lecture (formats à synthèse temps réel :
    ///     libopenmpt/UADE/ZXTune/SNDH) — WriteAt() est appelé au fil de l'eau depuis
    ///     le même point que SampleRingBuffer.Write(), avec la position absolue en
    ///     samples pour savoir dans quel bucket ranger les données.
    ///   - Remplissage COMPLET en une passe, en arrière-plan (fichiers audio
    ///     préexistants : wav/mp3/flac/ogg/m4a/aiff via NativeAudioPlayer) — un
    ///     Task.Run décode tout le fichier une fois via un lecteur NAudio séparé du
    ///     lecteur de lecture, sans gêner la lecture en cours.
    ///
    /// Conception "best effort" : pas de verrou global, juste des Interlocked/Volatile
    /// sur les compteurs de progression. De petites incohérences de lecture pendant
    /// une écriture concurrente sont acceptables pour un simple visuel d'ensemble —
    /// on privilégie l'absence de contention sur le thread audio.
    /// </summary>
    public sealed class WaveformOverviewBuffer
    {
        /// <summary>Nombre de colonnes de l'enveloppe — résolution fixe, indépendante de la durée du morceau.</summary>
        public const int Buckets = 1024;

        private readonly float[] _min = new float[Buckets];
        private readonly float[] _max = new float[Buckets];
        private long _totalSamplesEstimate = 1;
        private int  _highestBucket = -1;

        /// <summary>Dernier bucket effectivement rempli (-1 = rien encore) — sert au remplissage progressif à l'écran.</summary>
        public int HighestBucket => Volatile.Read(ref _highestBucket);

        /// <summary>True dès qu'au moins un bucket a été écrit.</summary>
        public bool HasData => HighestBucket >= 0;

        /// <summary>True quand l'enveloppe couvre tout le morceau (fin de décodage complet, ou lecture arrivée au bout).</summary>
        public bool IsComplete => HighestBucket >= Buckets - 1;

        /// <summary>Remet le buffer à zéro (nouveau morceau chargé).</summary>
        public void Reset()
        {
            Array.Clear(_min, 0, Buckets);
            Array.Clear(_max, 0, Buckets);
            Interlocked.Exchange(ref _highestBucket, -1);
        }

        /// <summary>
        /// À appeler une fois la durée du morceau connue (après LoadAsync), avant toute
        /// écriture — dimensionne la répartition des samples dans les buckets.
        /// </summary>
        public void SetDuration(double seconds, int sampleRate)
        {
            Reset();
            long total = (long)(Math.Max(0.1, seconds) * Math.Max(1, sampleRate));
            Interlocked.Exchange(ref _totalSamplesEstimate, Math.Max(1, total));
        }

        /// <summary>
        /// Écrit un bloc de samples stéréo à la position absolue donnée (en samples
        /// depuis le début du morceau). Mixdown mono (moyenne L/R) pour l'enveloppe.
        /// </summary>
        public void WriteAt(long samplePositionStart, float[] left, float[] right, int count)
        {
            long total = Volatile.Read(ref _totalSamplesEstimate);
            if (total <= 0) return;

            int maxSeen = -1;
            for (int i = 0; i < count; i++)
            {
                long pos = samplePositionStart + i;
                if (pos < 0) continue;
                int bucket = (int)(pos * Buckets / total);
                if (bucket < 0) bucket = 0;
                else if (bucket >= Buckets) bucket = Buckets - 1;

                float v = (left[i] + right[i]) * 0.5f;
                if (v < _min[bucket]) _min[bucket] = v;
                if (v > _max[bucket]) _max[bucket] = v;
                if (bucket > maxSeen) maxSeen = bucket;
            }

            if (maxSeen < 0) return;

            // Progression monotone (jamais en arrière) — suffisant pour piloter le
            // remplissage progressif à l'écran ; le décodage complet en arrière-plan
            // écrit de toute façon tous les buckets dans l'ordre.
            int prev;
            do
            {
                prev = Volatile.Read(ref _highestBucket);
                if (maxSeen <= prev) return;
            }
            while (Interlocked.CompareExchange(ref _highestBucket, maxSeen, prev) != prev);
        }

        /// <summary>Copie l'état courant de l'enveloppe pour un rendu thread-safe côté UI.</summary>
        public void CopySnapshot(float[] minOut, float[] maxOut)
        {
            Array.Copy(_min, minOut, Buckets);
            Array.Copy(_max, maxOut, Buckets);
        }
    }
}
