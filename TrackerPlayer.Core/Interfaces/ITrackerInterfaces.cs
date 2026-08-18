using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.Core.Interfaces
{
    /// <summary>
    /// Contrat d'un décodeur de fichier tracker.
    /// Chaque format (MOD, S3M, XM…) implémente ce contrat.
    /// </summary>
    public interface ITrackerDecoder
    {
        /// <summary>Extensions de fichiers supportées (ex: ".mod", ".s3m").</summary>
        string[] SupportedExtensions { get; }

        /// <summary>Nom lisible du format (ex: "ProTracker MOD").</summary>
        string FormatName { get; }

        /// <summary>Indique si ce décodeur peut ouvrir le fichier donné (signature magic bytes).</summary>
        bool CanDecode(Stream stream);

        /// <summary>
        /// Charge et analyse le fichier, retourne un <see cref="TrackerModule"/> complet.
        /// </summary>
        /// <param name="stream">Flux du fichier (positionné au début).</param>
        /// <param name="filePath">Chemin d'origine pour les logs / métadonnées.</param>
        Task<TrackerModule> DecodeAsync(Stream stream, string filePath, CancellationToken ct = default);
    }

    /// <summary>
    /// Contrat d'un moteur de lecture audio (libopenmpt, UADE, ZXTune…).
    /// </summary>
    public interface ITrackerPlayer : IDisposable
    {
        /// <summary>Formats gérés par ce player.</summary>
        TrackerFormat[] SupportedFormats { get; }

        /// <summary>Déclenché à chaque changement d'état de lecture (ligne, pattern, BPM…).</summary>
        event EventHandler<Models.PlaybackState>? StateChanged;

        /// <summary>Déclenché quand la lecture se termine naturellement.</summary>
        event EventHandler? PlaybackFinished;

        /// <summary>Charge un module déjà décodé.</summary>
        Task LoadAsync(TrackerModule module, CancellationToken ct = default);

        /// <summary>Lance ou reprend la lecture.</summary>
        void Play();

        /// <summary>Mets en pause.</summary>
        void Pause();

        /// <summary>Arrête et revient au début.</summary>
        void Stop();

        /// <summary>Saute à une position dans l'ordre de lecture (order index).</summary>
        void SeekToOrder(int orderIndex);

        /// <summary>Volume principal 0.0 – 1.0.</summary>
        float MasterVolume { get; set; }

        /// <summary>State courant.</summary>
        Models.PlaybackState CurrentState { get; }

        // 2026-07-30, retour utilisateur : certains modules UADE contiennent
        // plusieurs "subsongs" (cf. stderr "There are N subsongs in range [...]").
        // DemoBase les enchaînait déjà automatiquement (UadePlayer.OnSubsongFinished)
        // mais sans moyen de naviguer manuellement ni d'afficher l'info — d'où ce
        // contrat, implémenté réellement par UadePlayer (seul format concerné pour
        // l'instant) et en stub (1 subsong, sans effet) par les autres players.

        /// <summary>Nombre de subsongs du module courant (1 si non applicable/inconnu).</summary>
        int SubsongCount { get; }

        /// <summary>Index du subsong en cours de lecture (0-based).</summary>
        int CurrentSubsongIndex { get; }

        /// <summary>Bascule vers le subsong donné (0-based). Sans effet si non supporté ou hors bornes.</summary>
        void SelectSubsong(int index);
    }

    /// <summary>
    /// Service de haut niveau : détecte le format, choisit le bon décodeur et le bon player.
    /// C'est le point d'entrée principal pour les consommateurs de la bibliothèque.
    /// </summary>
    public interface ITrackerService
    {
        /// <summary>
        /// Ouvre un fichier tracker : détecte le format, decode les métadonnées / patterns,
        /// et initialise le player approprié.
        /// </summary>
        /// <param name="filePath">Chemin absolu du fichier.</param>
        Task<(TrackerModule Module, ITrackerPlayer Player)> OpenAsync(
            string filePath, CancellationToken ct = default);

        /// <summary>Liste des extensions supportées par tous les décodeurs enregistrés.</summary>
        string[] AllSupportedExtensions { get; }
    }
}
