using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.Core.Decoders
{
    /// <summary>
    /// Décodeur pour le format STM (ScreamTracker 2).
    /// Structure header STM :
    ///   0x00 : 20 bytes  titre
    ///   0x14 :  8 bytes  magic "!Scream!" | "BMOD2STM" | "WUZAMOD!"
    ///   0x1C :  1 byte   version major
    ///   0x1D :  1 byte   version minor
    ///   0x1E :  1 byte   file type (2 = module)
    ///   0x1F :  1 byte   initial tempo
    ///   0x20 :  1 byte   num patterns
    ///   0x21 :  1 byte   master volume
    ///   0x22 :  8 bytes  reserved
    ///   0x2A : 31×32b    instruments
    ///   0x40A: 128 bytes order table (99 = fin)
    ///   0x48A: patterns  (64 rows × 4 ch × 4 bytes = 1024 bytes chacun)
    /// Les patterns et samples sont remplis par libopenmpt.EnrichModule.
    /// </summary>
    public class StmDecoder : ITrackerDecoder
    {
        public string[] SupportedExtensions => [".stm", ".st2"];
        public string   FormatName          => "ScreamTracker 2 STM";

        private const int INST_OFFSET  = 0x2A;
        private const int ORDER_OFFSET = 0x2A + 31 * 32;   // 0x40A
        private const int ORDER_COUNT  = 128;

        public bool CanDecode(Stream stream)
        {
            if (stream.Length < 32) return false;
            long pos = stream.Position;
            stream.Position = 0x14;
            var sig = new byte[8];
            stream.Read(sig, 0, 8);
            stream.Position = pos;
            string s = Encoding.ASCII.GetString(sig);
            return s == "!Scream!" || s == "BMOD2STM" || s == "WUZAMOD!";
        }

        public Task<TrackerModule> DecodeAsync(Stream stream, string filePath,
            CancellationToken ct = default)
        {
            stream.Position = 0;
            using var br = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

            // Titre (20 bytes)
            var titleBytes = br.ReadBytes(20);
            string title = Encoding.Latin1.GetString(titleBytes).TrimEnd('\0').Trim();

            // Magic (8), version major, minor, file type
            br.ReadBytes(8);  // magic
            int verMaj  = br.ReadByte();
            int verMin  = br.ReadByte();
            int fileType= br.ReadByte();

            // Paramètres
            int initTempo = br.ReadByte();  // 0x1F
            int numPat    = br.ReadByte();  // 0x20
            int masterVol = br.ReadByte();  // 0x21

            // Order table à 0x40A
            stream.Position = ORDER_OFFSET;
            var module = new TrackerModule
            {
                Format     = TrackerFormat.STM,
                FilePath   = filePath,
                FileSize   = stream.Length,
                Title      = title,
                Channels   = 4,
                InitialBpm = initTempo > 0 ? initTempo * 2 : 125,
            };

            // Order list : stopper au marqueur 99
            for (int i = 0; i < ORDER_COUNT; i++)
            {
                int o = br.ReadByte();
                if (o >= 99) break;
                module.OrderList.Add(o);
            }

            // Patterns et samples : libopenmpt les remplit via EnrichModule
            return Task.FromResult(module);
        }
    }
}
