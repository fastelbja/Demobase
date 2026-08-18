using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.Core.Decoders
{
    /// <summary>
    /// Décodeur pour le format S3M (ScreamTracker 3).
    /// Référence : https://wiki.multimedia.cx/index.php/S3M_Format
    /// </summary>
    public class S3mDecoder : ITrackerDecoder
    {
        public string[] SupportedExtensions => [".s3m"];
        public string FormatName => "ScreamTracker 3 S3M";

        public bool CanDecode(Stream stream)
        {
            if (stream.Length < 48) return false;
            long pos = stream.Position;
            stream.Position = 44;
            byte[] sig = new byte[4];
            stream.Read(sig, 0, 4);
            stream.Position = pos;
            return Encoding.ASCII.GetString(sig) == "SCRM";
        }

        public Task<TrackerModule> DecodeAsync(Stream stream, string filePath, CancellationToken ct = default)
        {
            stream.Position = 0;
            using var br = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

            var module = new TrackerModule
            {
                Format = TrackerFormat.S3M,
                FilePath = filePath,
                FileSize = stream.Length
            };

            // ── En-tête (96 octets) ──────────────────────────────────────
            module.Title = ReadFixedString(br, 28);
            br.ReadByte();  // 0x1A
            br.ReadByte();  // type = 16
            br.ReadUInt16(); // reserved

            ushort orderCount = br.ReadUInt16();
            ushort instrCount = br.ReadUInt16();
            ushort patternCount = br.ReadUInt16();
            ushort flags = br.ReadUInt16();
            ushort trackerVersion = br.ReadUInt16();
            ushort sampleType = br.ReadUInt16();
            br.ReadBytes(4);  // "SCRM"
            byte globalVolume = br.ReadByte();
            byte initialSpeed = br.ReadByte();
            byte initialTempo = br.ReadByte();
            byte masterVolume = br.ReadByte();
            br.ReadBytes(12); // ultra click removal + default channel panning etc.

            module.GlobalVolume = globalVolume;
            module.InitialSpeed = initialSpeed;
            module.InitialBpm = initialTempo;

            // Canaux actifs
            byte[] channelSettings = br.ReadBytes(32);
            int channelCount = 0;
            for (int i = 0; i < 32; i++)
                if (channelSettings[i] < 16 || channelSettings[i] == 255) channelCount++;
            module.Channels = Math.Max(channelCount, 4);

            // ── Order list ───────────────────────────────────────────────
            byte[] orders = br.ReadBytes(orderCount);
            for (int i = 0; i < orderCount; i++)
                if (orders[i] < 254) module.OrderList.Add(orders[i]);

            // ── Pointeurs instruments & patterns (paragraphes × 16) ──────
            ushort[] instrPtrs = new ushort[instrCount];
            for (int i = 0; i < instrCount; i++) instrPtrs[i] = br.ReadUInt16();

            ushort[] patPtrs = new ushort[patternCount];
            for (int i = 0; i < patternCount; i++) patPtrs[i] = br.ReadUInt16();

            // ── Instruments ──────────────────────────────────────────────
            for (int i = 0; i < instrCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = instrPtrs[i] * 16L;
                byte instrType = br.ReadByte();
                string dosName = ReadFixedString(br, 12);
                uint dataParaHi = br.ReadByte();
                uint dataParaLo = br.ReadUInt16();
                uint length = br.ReadUInt32();
                uint loopStart = br.ReadUInt32();
                uint loopEnd = br.ReadUInt32();
                byte vol = br.ReadByte();
                br.ReadByte(); // dsk
                byte pack = br.ReadByte();
                byte sFlags = br.ReadByte();
                uint c2spd = br.ReadUInt32();
                br.ReadBytes(12); // reserved
                string name = ReadFixedString(br, 28);
                br.ReadBytes(4);  // "SCRS" / "SCRI"

                module.Samples.Add(new TrackerSample
                {
                    Index = i,
                    Name = name.Length > 0 ? name : dosName,
                    Length = (int)length,
                    LoopStart = (int)loopStart,
                    LoopLength = (int)(loopEnd - loopStart),
                    Volume = vol
                });
            }

            // ── Patterns ─────────────────────────────────────────────────
            for (int p = 0; p < patternCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                if (patPtrs[p] == 0) { module.Patterns.Add(new TrackerPattern(p, 64, module.Channels)); continue; }

                stream.Position = patPtrs[p] * 16L;
                ushort packedLen = br.ReadUInt16();
                byte[] data = br.ReadBytes(packedLen);

                var pattern = new TrackerPattern(p, 64, module.Channels);
                int idx = 0, row = 0;

                while (row < 64 && idx < data.Length)
                {
                    byte what = data[idx++];
                    if (what == 0) { row++; continue; }

                    int channel = what & 0x1F;
                    int note = 0, instrument = 0, volume = -1, effect = 0, param = 0;

                    if ((what & 0x20) != 0 && idx + 1 < data.Length)
                    {
                        byte noteB = data[idx++];
                        instrument = data[idx++];
                        note = noteB == 255 ? 0 : (noteB & 0x0F) + (noteB >> 4) * 12 + 1;
                    }
                    if ((what & 0x40) != 0 && idx < data.Length) volume = data[idx++];
                    if ((what & 0x80) != 0 && idx + 1 < data.Length)
                    {
                        effect = data[idx++];
                        param = data[idx++];
                    }

                    if (channel < module.Channels)
                        pattern.Cells[row, channel] = new PatternCell
                        { Note = note, Instrument = instrument, Volume = volume, Effect = effect, EffectParam = param };
                }

                module.Patterns.Add(pattern);
            }

            double rowsTotal = module.OrderList.Count * 64.0;
            module.DurationSeconds = rowsTotal * module.InitialSpeed / (module.InitialBpm * 0.4);

            return Task.FromResult(module);
        }

        private static string ReadFixedString(BinaryReader br, int length)
        {
            byte[] bytes = br.ReadBytes(length);
            int end = Array.IndexOf(bytes, (byte)0);
            return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end).Trim();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Détecteur de format — choisit le bon décodeur
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Détecte automatiquement le format d'un fichier tracker et retourne
    /// le décodeur approprié parmi ceux enregistrés.
    /// </summary>
    public static class FormatDetector
    {
        /// <summary>
        /// Retourne le <see cref="TrackerFormat"/> d'un fichier à partir de son extension
        /// et, si nécessaire, de ses magic bytes.
        /// </summary>
        public static TrackerFormat DetectFormat(string filePath, Stream stream)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".mod" or ".nst" or ".stk" => TrackerFormat.MOD,
                ".s3m"           => TrackerFormat.S3M,
                ".xm"            => TrackerFormat.XM,
                ".it"            => TrackerFormat.IT,
                ".sid" or ".psid" => TrackerFormat.SID,
                ".ahx"           => TrackerFormat.AHX,
                ".ay"            => TrackerFormat.AY,
                ".ym"            => TrackerFormat.YM,
                ".pt3"           => TrackerFormat.PT3,
                ".vtx"           => TrackerFormat.VTX,
                ".stc"           => TrackerFormat.STC,
                ".psg"           => TrackerFormat.PSG,
                ".gbs"           => TrackerFormat.GBS,
                ".nsf"           => TrackerFormat.NSF,
                ".vgm" or ".vgz" => TrackerFormat.VGM,
                _ => TrackerFormat.Unknown
            };
        }

        /// <summary>Groupes de formats supportés nativement (sans plugin externe).</summary>
        public static bool IsNativeFormat(TrackerFormat fmt) =>
            fmt is TrackerFormat.MOD or TrackerFormat.S3M or TrackerFormat.XM or TrackerFormat.IT;

        /// <summary>Formats nécessitant UADE (lecteur Amiga).</summary>
        public static bool IsUadeFormat(TrackerFormat fmt) =>
            fmt is TrackerFormat.SID or TrackerFormat.AHX or TrackerFormat.TFMX or TrackerFormat.HVSC;

        /// <summary>Formats nécessitant ZXTune.</summary>
        public static bool IsZxTuneFormat(TrackerFormat fmt) =>
            fmt is TrackerFormat.AY or TrackerFormat.YM or TrackerFormat.PT3
                or TrackerFormat.VTX or TrackerFormat.STC or TrackerFormat.PSG;
    }
}
