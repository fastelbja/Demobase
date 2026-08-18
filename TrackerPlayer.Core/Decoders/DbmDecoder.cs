using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.Core.Decoders
{
    /// <summary>
    /// Décodeur pour le format DBM (DigiBooster Pro).
    /// Produit uniquement les métadonnées de base — les patterns et samples
    /// sont remplis par libopenmpt via NativeTrackerPlayer.EnrichModule.
    /// Magic : "DBM0" en début de fichier.
    /// Supporte jusqu'à 32 canaux.
    /// </summary>
    public class DbmDecoder : ITrackerDecoder
    {
        public string[] SupportedExtensions => [".dbm"];
        public string   FormatName          => "DigiBooster Pro DBM";

        public bool CanDecode(Stream stream)
        {
            if (stream.Length < 8) return false;
            long pos = stream.Position;
            stream.Position = 0;
            var sig = new byte[4];
            stream.Read(sig, 0, 4);
            stream.Position = pos;
            return Encoding.ASCII.GetString(sig) == "DBM0";
        }

        public Task<TrackerModule> DecodeAsync(Stream stream, string filePath,
            CancellationToken ct = default)
        {
            stream.Position = 0;
            using var br = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

            // Header DBM IFF-style :
            // 4 bytes "DBM0"
            // 4 bytes chunk size (big-endian)
            // 2 bytes tracker version (big-endian)
            // 2 bytes reserved
            // 20 bytes song name
            stream.Position = 0;
            br.ReadBytes(4);  // "DBM0"
            // chunk size big-endian
            int chunkSizeHi = br.ReadByte(); int chunkSizeLo = br.ReadBytes(3)[2];
            stream.Position = 8;

            // Nom (20 bytes, terminé par \0)
            var nameBytes = br.ReadBytes(20);
            string title = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0').Trim();

            var module = new TrackerModule
            {
                Format   = TrackerFormat.DBM,
                FilePath = filePath,
                FileSize = stream.Length,
                Title    = title,
                Channels = 16,  // DBM supporte jusqu'à 32 canaux, défaut 16
            };

            // Patterns, samples, order list : libopenmpt les remplit dans EnrichModule
            return Task.FromResult(module);
        }
    }
}
