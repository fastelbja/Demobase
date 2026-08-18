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
    /// Décodeur pour le format XM (Extended Module) de FastTracker 2.
    /// Référence : https://wiki.openmpt.org/Development:_XM_Format
    /// </summary>
    public class XmDecoder : ITrackerDecoder
    {
        private const string XmSignature = "Extended Module: ";

        public string[] SupportedExtensions => [".xm"];
        public string FormatName => "FastTracker 2 XM";

        public bool CanDecode(Stream stream)
        {
            if (stream.Length < 60) return false;
            long pos = stream.Position;
            stream.Position = 0;
            byte[] sig = new byte[17];
            stream.Read(sig, 0, 17);
            stream.Position = pos;
            return Encoding.ASCII.GetString(sig) == XmSignature;
        }

        public Task<TrackerModule> DecodeAsync(Stream stream, string filePath, CancellationToken ct = default)
        {
            stream.Position = 0;
            using var br = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

            var module = new TrackerModule
            {
                Format = TrackerFormat.XM,
                FilePath = filePath,
                FileSize = stream.Length
            };

            // ── En-tête ──────────────────────────────────────────────────
            string sig = Encoding.ASCII.GetString(br.ReadBytes(17));     // "Extended Module: "
            module.Title = ReadFixedString(br, 20);                       // Module name
            br.ReadByte();                                                  // 0x1A
            string tracker = ReadFixedString(br, 20);                     // Tracker name
            module.Author = tracker;
            ushort version = br.ReadUInt16();                              // Version (0x0104)

            uint headerSize = br.ReadUInt32();
            ushort songLength = br.ReadUInt16();
            ushort restartPos = br.ReadUInt16();
            ushort channels = br.ReadUInt16();
            ushort numPatterns = br.ReadUInt16();
            ushort numInstruments = br.ReadUInt16();
            ushort flags = br.ReadUInt16();
            ushort defaultTempo = br.ReadUInt16();
            ushort defaultBpm = br.ReadUInt16();

            module.Channels = channels;
            module.InitialSpeed = defaultTempo;
            module.InitialBpm = defaultBpm;

            // Order list (256 octets dans l'en-tête)
            byte[] orders = br.ReadBytes(256);
            for (int i = 0; i < songLength; i++)
                module.OrderList.Add(orders[i]);

            // Saute au début des patterns (headerSize est relatif à l'offset 60)
            stream.Position = 60 + headerSize;

            // ── Patterns ─────────────────────────────────────────────────
            for (int p = 0; p < numPatterns; p++)
            {
                ct.ThrowIfCancellationRequested();

                long patStart = stream.Position;
                uint patHeaderSize = br.ReadUInt32();
                byte packType = br.ReadByte();      // toujours 0
                ushort numRows = br.ReadUInt16();
                ushort packedSize = br.ReadUInt16();

                stream.Position = patStart + patHeaderSize;

                var pattern = new TrackerPattern(p, numRows, channels);

                if (packedSize == 0)
                {
                    // Pattern vide
                }
                else
                {
                    byte[] packedData = br.ReadBytes(packedSize);
                    int idx = 0;
                    for (int row = 0; row < numRows && idx < packedData.Length; row++)
                        for (int ch = 0; ch < channels && idx < packedData.Length; ch++)
                        {
                            byte first = packedData[idx++];
                            bool packed = (first & 0x80) != 0;

                            int note = 0, instrument = 0, volume = -1, effect = 0, param = 0;

                            if (packed)
                            {
                                if ((first & 0x01) != 0) note = packedData[idx++];
                                if ((first & 0x02) != 0) instrument = packedData[idx++];
                                if ((first & 0x04) != 0) volume = packedData[idx++];
                                if ((first & 0x08) != 0) effect = packedData[idx++];
                                if ((first & 0x10) != 0) param = packedData[idx++];
                            }
                            else
                            {
                                note = first;
                                instrument = idx < packedData.Length ? packedData[idx++] : 0;
                                volume = idx < packedData.Length ? packedData[idx++] : -1;
                                effect = idx < packedData.Length ? packedData[idx++] : 0;
                                param = idx < packedData.Length ? packedData[idx++] : 0;
                            }

                            // Note 97 = Key Off
                            int noteVal = note is > 0 and < 97 ? note : (note == 97 ? -1 : 0);

                            pattern.Cells[row, ch] = new PatternCell
                            {
                                Note = noteVal < 0 ? 0 : noteVal,
                                Instrument = instrument,
                                Volume = volume > 0x50 ? -1 : volume,
                                Effect = effect,
                                EffectParam = param
                            };
                        }
                }

                module.Patterns.Add(pattern);
            }

            // ── Instruments (métadonnées seulement) ──────────────────────
            for (int i = 0; i < numInstruments; i++)
            {
                ct.ThrowIfCancellationRequested();
                long instrStart = stream.Position;

                uint instrSize = br.ReadUInt32();
                string instrName = ReadFixedString(br, 22);
                byte instrType = br.ReadByte();
                ushort numSamples = br.ReadUInt16();

                var sample = new TrackerSample
                {
                    Index = i,
                    Name = instrName
                };

                if (numSamples > 0)
                {
                    uint sampleHeaderSize = br.ReadUInt32();
                    // Saute note-to-sample, volume/panning envelopes, etc.
                    stream.Position = instrStart + instrSize;

                    // Lit les headers de samples — collecte les longueurs pour sauter les données
                    var sampleLengths = new uint[numSamples];
                    for (int s = 0; s < numSamples; s++)
                    {
                        long shStart = stream.Position;
                        uint sampleLen = br.ReadUInt32();
                        uint loopStart = br.ReadUInt32();
                        uint loopLen = br.ReadUInt32();
                        byte vol = br.ReadByte();
                        sbyte fine = (sbyte)br.ReadByte();
                        byte sType = br.ReadByte();
                        byte pan = br.ReadByte();
                        sbyte relNote = (sbyte)br.ReadByte();
                        br.ReadByte(); // reserved
                        string sName = ReadFixedString(br, 22);

                        sampleLengths[s] = sampleLen;

                        if (s == 0)
                        {
                            sample.Length = (int)sampleLen;
                            sample.LoopStart = (int)loopStart;
                            sample.LoopLength = (int)loopLen;
                            sample.Volume = vol;
                            sample.FineTune = fine;
                        }

                        stream.Position = shStart + sampleHeaderSize;
                    }

                    // Saute les données de TOUS les samples pour arriver au prochain instrument
                    long totalSampleData = 0;
                    foreach (uint l in sampleLengths) totalSampleData += l;
                    stream.Position += totalSampleData;
                }
                else
                {
                    stream.Position = instrStart + instrSize;
                }

                module.Samples.Add(sample);
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
}
