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
    /// Décodeur natif pour le format MOD (ProTracker / Noisetracker / Soundtracker).
    /// Supporte les MOD 31-samples (ProTracker, M.K.) et 15-samples (Soundtracker original).
    /// Référence : https://wiki.multimedia.cx/index.php/Protracker_Module
    /// </summary>
    public class ModDecoder : ITrackerDecoder
    {
        public string[] SupportedExtensions => [".mod", ".nst", ".wow", ".stk"];
        public string FormatName => "ProTracker MOD";

        // Tags magic à l'offset 0x438 pour les MOD 31-samples
        // 2026-07-31, retour utilisateur (fichier réel "mattis.floor mix.mod", tag "10CH"
        // vérifié à l'octet près à l'offset 0x438) : "5CHN"/"7CHN"/"9CHN" ajoutés — présents
        // dans la doc de référence (wiki.multimedia.cx/Protracker_Module : "extension
        // TakeTracker pour 5, 7 et 9 canaux") mais absents d'ici jusqu'à présent. Les tags
        // "xxCH"/"xxCN" (10 à 32 canaux, xx pair) ne sont PAS listés ici — il y en a une
        // douzaine de valeurs possibles — cf. TryParseChannelCountTag ci-dessous, qui les
        // détecte par motif plutôt que par égalité de chaîne.
        private static readonly string[] KnownTags =
        [
            "M.K.", "M!K!", "M&K!", "N.T.",
            "2CHN", "4CHN", "5CHN", "6CHN", "7CHN", "8CHN", "9CHN",
            "FLT4", "FLT8",
            "OCTA", "CD81", "OKTA",
            "16CN", "32CN",
            "FEST", "FIST", "EXO4", "EXO8",
            "TDZ1", "TDZ2", "TDZ3",
        ];

        /// <summary>
        /// Vrai si <paramref name="tag"/> suit la convention "xxCH"/"xxCN" (MOD 10+ canaux,
        /// xx = nombre décimal pair de 10 à 32) — cf. wiki.multimedia.cx/Protracker_Module :
        /// "xxCH - a 10+ channel MOD, xx being a decimal number. FastTracker will deal with
        /// these as long as x is an even number no greater than 32" (idem "xxCN", variante
        /// TakeTracker). Non listée dans <see cref="KnownTags"/> (chaînes fixes) car il y a
        /// une douzaine de valeurs xx possibles (10,12,14,...,32) plutôt qu'une poignée de
        /// tags nommés — détection par motif à la place.
        /// 2026-07-31, retour utilisateur : fichier réel avec tag "10CH" — jusqu'ici
        /// entièrement non reconnu (ni dans KnownTags, ni dans le switch de canaux), donc
        /// classé à tort "Untagged31" (variant sans tag), ce qui causait DEUX bugs cumulés :
        /// nombre de canaux figé à 4 au lieu de 10 (cf. DecodeAsync, ancien "_ => 4"), ET les
        /// 4 octets du tag "10CH" jamais consommés → lecture des patterns décalée de 4 octets
        /// (le "T" du texte "10CH" étant alors lu comme le 1er octet du pattern 0).
        /// </summary>
        private static bool TryParseChannelCountTag(string tag, out int channels)
        {
            channels = 0;
            if (tag.Length != 4) return false;
            if (!char.IsDigit(tag[0]) || !char.IsDigit(tag[1])) return false;
            string suffix = tag[2..];
            if (suffix != "CH" && suffix != "CN") return false;
            if (!int.TryParse(tag[..2], out int n)) return false;
            if (n < 10 || n > 32 || n % 2 != 0) return false;
            channels = n;
            return true;
        }

        /// <summary>Variante MOD détectée — détermine le nombre de samples, si un tag
        /// 4 caractères est présent, et le nombre de canaux par défaut.</summary>
        private enum ModVariant { Unknown, Tagged31, Untagged31, Soundtracker15 }

        /// <summary>
        /// Détection UNIQUE partagée par <see cref="CanDecode"/> et <see cref="DecodeAsync"/>
        /// (auparavant dupliquée séparément dans les deux méthodes, avec un risque réel de les
        /// laisser diverger). Ne modifie jamais durablement stream.Position (restaurée à la fin).
        ///
        /// 2026-07-31, retour utilisateur (fichier réel "doitnow.mod", "Soundtracker 2.6" — "le
        /// pattern s'affiche bien dans openmpt mais pas dans demobase") : la seule branche
        /// existante pour un fichier SANS tag connu à 0x438 était l'heuristique 15-samples —
        /// or ce fichier est en réalité un MOD 31-samples SANS tag (vieux Noisetracker/
        /// Soundtracker 2.x, antérieur à la convention de tag "M.K." introduite plus tard).
        /// L'heuristique 15-samples ne valide QUE les 15 premiers slots (tous plausibles ici,
        /// puisqu'ils font aussi partie d'un vrai en-tête 31-samples) sans jamais vérifier la
        /// cohérence de ce qui suit — elle acceptait donc ce fichier à tort, avec une longueur
        /// de morceau lue à un mauvais offset (résultat observé : 0, donc AUCUN ordre, un seul
        /// pattern "fantôme" lu à un offset qui tombe en fait au milieu des samples 16-30
        /// réels). Diagnostiqué en reconstruisant le fichier deux façons (script Python) :
        /// l'hypothèse 31-samples-sans-tag donne une longueur de morceau (67) et une table
        /// d'ordres (valeurs basses, croissantes, cohérentes : 0,1,2,0,0,1,2,0,3,1,2...) tout à
        /// fait plausibles pour un vrai morceau, alors que l'hypothèse 15-samples donne une
        /// longueur de morceau de 0 (impossible pour un vrai morceau). Fix : tenter D'ABORD le
        /// 31-samples-sans-tag (validé par <see cref="TryValidateSampleTable"/>, qui exige EN
        /// PLUS une longueur de morceau dans l'intervalle valide [1,128] — pas seulement des
        /// volumes/noms plausibles comme avant) avant de retomber sur 15-samples.
        /// </summary>
        private static ModVariant DetectVariant(Stream stream, out int songLength)
        {
            songLength = 0;
            long pos = stream.Position;
            try
            {
                if (stream.Length < 20) return ModVariant.Unknown;

                // Test 0 : exclusion ST26/Ice Tracker ("MTN\0"/"IT10" à l'offset 1464) —
                // 2026-08-07, retour utilisateur (fichier réel "doitnow.mod", MÊME fichier
                // que le correctif "Untagged31" du 2026-07-31 ci-dessous : "les patterns ne
                // s'affichent pas correctement [...] il me semble qu'on avait déjà corrigé
                // ce problème"). Le correctif du 07-31 a bien réglé le cas qu'il ciblait
                // (repli 15-samples à tort) mais a introduit un nouveau faux positif pour
                // CE fichier précis : il s'agit en réalité du format "SoundTracker 2.6"
                // (alias "Ice Tracker"/ST26 côté libopenmpt, cf. Load_ice.cpp — vérifié en
                // interrogeant libopenmpt directement : type="st26", 4 canaux, 67 patterns,
                // ordre [0..66] séquentiel), qui a exactement les 31 mêmes en-têtes de
                // sample que ProTracker (d'où le passage à tort de TryValidateSampleTable)
                // MAIS stocke ensuite les patterns tout autrement : après les 31 samples
                // (offset 950), 1 octet "nombre d'ordres" + 1 octet "nombre de tracks"
                // (PAS une "longueur de morceau + position de reprise" classique), puis une
                // table de 512 octets (128 ordres × 4 canaux) d'INDEX DE TRACKS réutilisables
                // (PAS une table d'ordres classique de 128 octets), puis le tag magique
                // "MTN\0"/"IT10" à l'offset 950+2+512=1464, et enfin les tracks eux-mêmes
                // (chacun 64 lignes × 4 octets, indexés par la table précédente) à partir de
                // 1468. Notre heuristique 31-samples-sans-tag lisait donc la table de tracks
                // comme si c'était une table d'ordres classique (valeurs basses, croissantes,
                // donc "plausibles" à tort) et les tracks comme si c'étaient des patterns
                // classiques dès l'offset 1080 (bien avant leur vraie position 1468) —
                // d'où des periods/notes n'importe quoi observés en pattern 0. Plutôt que
                // d'implémenter ce format alternatif ici, on l'exclut explicitement (même
                // magic bytes que la détection de référence libopenmpt) pour laisser
                // NativeTrackerPlayer/libopenmpt le décoder nativement — déjà vérifié
                // capable de le faire correctement (mécanisme de repli déjà en place pour
                // les formats sans décodeur C# dédié, cf. TrackerService.OpenAsync).
                if (stream.Length >= 1468)
                {
                    stream.Position = 1464;
                    byte[] iceMagic = new byte[4];
                    stream.Read(iceMagic, 0, 4);
                    if ((iceMagic[0] == 'M' && iceMagic[1] == 'T' && iceMagic[2] == 'N' && iceMagic[3] == 0)
                        || (iceMagic[0] == 'I' && iceMagic[1] == 'T' && iceMagic[2] == '1' && iceMagic[3] == '0'))
                    {
                        return ModVariant.Unknown;
                    }
                }

                // Test 1 : tag ProTracker/Noisetracker connu à l'offset 0x438 (31 samples)
                if (stream.Length >= 0x43C)
                {
                    stream.Position = 0x438;
                    byte[] tag = new byte[4];
                    stream.Read(tag, 0, 4);
                    string s = Encoding.ASCII.GetString(tag);
                    if (Array.Exists(KnownTags, t => t == s) || TryParseChannelCountTag(s, out _))
                    {
                        // Longueur de morceau lue séparément (offset fixe 20+31*30=950),
                        // sans validation stricte ici : un tag connu est déjà une preuve
                        // suffisante à lui seul, cf. TryValidateSampleTable pour le cas
                        // sans tag ci-dessous où cette validation devient nécessaire.
                        stream.Position = 950;
                        songLength = Math.Min(Math.Max(stream.ReadByte(), 0), 128);
                        return ModVariant.Tagged31;
                    }
                }

                // Test 2 : 31 samples SANS tag (vieux Noisetracker/Soundtracker 2.x — cf.
                // commentaire de la méthode). Essayé AVANT le 15-samples : un vrai fichier
                // 31-samples fait toujours passer le test 15-samples aussi (les 15 premiers
                // slots sont identiques dans les deux interprétations), donc l'ordre importe.
                if (stream.Length >= 20 + 31 * 30 + 2 + 128
                    && TryValidateSampleTable(stream, 31, out int sl31))
                {
                    songLength = sl31;
                    return ModVariant.Untagged31;
                }

                // Test 3 : 15 samples (Soundtracker original)
                if (stream.Length >= 20 + 15 * 30 + 2 + 128
                    && TryValidateSampleTable(stream, 15, out int sl15))
                {
                    songLength = sl15;
                    return ModVariant.Soundtracker15;
                }

                return ModVariant.Unknown;
            }
            finally { stream.Position = pos; }
        }

        /// <summary>
        /// Vérifie qu'interpréter le flux avec <paramref name="sampleCount"/> échantillons
        /// (30 octets chacun, à partir de l'offset 20) donne une table plausible : tous les
        /// volumes ≤ 64, noms de sample majoritairement imprimables/nuls (≥90%, cf. retour
        /// utilisateur "Ace Tracker" du 2026-07-30), ET longueur de morceau (1er octet juste
        /// après la table) dans l'intervalle valide [1,128] — ce dernier critère, absent avant
        /// le 2026-07-31, est ce qui permet de départager 15 vs 31 samples pour un fichier sans
        /// tag (cf. DetectVariant).
        /// </summary>
        private static bool TryValidateSampleTable(Stream stream, int sampleCount, out int songLength)
        {
            songLength = 0;
            int printableNameBytes = 0, totalNameBytes = 0;
            var nameBytes = new byte[22];
            for (int i = 0; i < sampleCount; i++)
            {
                long sampleOffset = 20 + i * 30;
                stream.Position = sampleOffset;
                stream.Read(nameBytes, 0, 22);
                foreach (var b in nameBytes)
                {
                    totalNameBytes++;
                    if (b == 0 || (b >= 0x20 && b < 0x7F)) printableNameBytes++;
                }

                stream.Position = sampleOffset + 25; // offset volume (après nom22+len2+fine1)
                int vol = stream.ReadByte();
                if (vol < 0 || vol > 64) return false;
            }
            if (totalNameBytes == 0 || printableNameBytes * 100 < totalNameBytes * 90) return false;

            long songLenOffset = 20 + sampleCount * 30L;
            stream.Position = songLenOffset;
            int sl = stream.ReadByte();
            if (sl < 1 || sl > 128) return false;
            songLength = sl;
            return true;
        }

        public bool CanDecode(Stream stream) => DetectVariant(stream, out _) != ModVariant.Unknown;

        public Task<TrackerModule> DecodeAsync(Stream stream, string filePath, CancellationToken ct = default)
        {
            // ── Détection AVANT de créer le BinaryReader ──────────────────────
            // CRITIQUE : on lit directement via stream (pas BinaryReader) car
            // BinaryReader bufferise 4096 octets et br.BaseStream.Position = 0
            // ne réinitialise pas son buffer interne → les lectures suivantes
            // partiraient de la mauvaise position.
            stream.Position = 0;
            var variant = DetectVariant(stream, out _); // songLength relu ci-dessous via BinaryReader
            int sampleCount = variant == ModVariant.Soundtracker15 ? 15 : 31;
            bool hasTag     = variant == ModVariant.Tagged31;

            // ── BinaryReader créé APRÈS la détection, positionné à 0 ──────────
            stream.Position = 0;
            using var br = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);

            var module = new TrackerModule
            {
                Format   = TrackerFormat.MOD,
                FilePath = filePath,
                FileSize = stream.Length
            };

            // ── Titre (20 octets) ─────────────────────────────────────────────
            module.Title = ReadFixedString(br, 20);

            // ── Samples (sampleCount × 30 octets) ────────────────────────────
            for (int i = 0; i < sampleCount; i++)
            {
                var sample = new TrackerSample { Index = i };
                sample.Name       = ReadFixedString(br, 22);
                sample.Length     = ReadBigEndianUInt16(br) * 2;
                sample.FineTune   = (sbyte)(br.ReadByte() & 0x0F);
                sample.Volume     = br.ReadByte();
                sample.LoopStart  = ReadBigEndianUInt16(br) * 2;
                sample.LoopLength = ReadBigEndianUInt16(br) * 2;
                module.Samples.Add(sample);
            }

            // ── Song length + restart position ────────────────────────────────
            int songLength = Math.Min((int)br.ReadByte(), 128);
            br.ReadByte(); // restart position (ignoré)

            // ── Order list (128 octets) ───────────────────────────────────────
            byte[] orders = br.ReadBytes(128);
            int maxPattern = 0;
            for (int i = 0; i < songLength; i++)
            {
                module.OrderList.Add(orders[i]);
                if (orders[i] > maxPattern) maxPattern = orders[i];
            }

            // ── Tag 4 caractères + nombre de canaux ───────────────────────────
            // 2026-07-31 : seul un tag RÉELLEMENT présent (ModVariant.Tagged31) fait
            // consommer ces 4 octets — un 31-samples SANS tag (Untagged31) enchaîne
            // directement sur les patterns, comme le 15-samples (sinon les patterns
            // seraient lus 4 octets trop loin).
            if (hasTag)
            {
                string tag = Encoding.ASCII.GetString(br.ReadBytes(4));
                // 2026-07-31, retour utilisateur ("mattis.floor mix.mod" affiche 10 canaux
                // dans le titre mais la vue ProTracker n'en montre que 4") : "5CHN"/"7CHN"/
                // "9CHN" et "TDZ1"/"TDZ2"/"TDZ3" ajoutés (tags documentés mais jusqu'ici
                // absents du switch, retombaient sur le défaut 4 malgré un tag reconnu) ;
                // le cas par défaut délègue maintenant à TryParseChannelCountTag pour les
                // tags "xxCH"/"xxCN" (10 à 32 canaux) au lieu de fixer 4 à tort.
                module.Channels = tag switch
                {
                    "5CHN"                                => 5,
                    "6CHN"                                => 6,
                    "7CHN"                                => 7,
                    "8CHN" or "OCTA" or "CD81" or "OKTA" => 8,
                    "9CHN"                                => 9,
                    "TDZ1"                                => 1,
                    "TDZ2"                                => 2,
                    "TDZ3"                                => 3,
                    "16CN"                                => 16,
                    "32CN"                                => 32,
                    _ => TryParseChannelCountTag(tag, out int n) ? n : 4
                };
            }
            else
            {
                module.Channels = 4; // Soundtracker (15 ou 31 samples sans tag) = 4 canaux
            }

            // ── Patterns (64 rows × channels × 4 octets) ─────────────────────
            for (int p = 0; p <= maxPattern; p++)
            {
                ct.ThrowIfCancellationRequested();
                var pattern = new TrackerPattern(p, 64, module.Channels);

                for (int row = 0; row < 64; row++)
                    for (int ch = 0; ch < module.Channels; ch++)
                    {
                        byte b0 = br.ReadByte();
                        byte b1 = br.ReadByte();
                        byte b2 = br.ReadByte();
                        byte b3 = br.ReadByte();

                        int sampleNum = (b0 & 0xF0) | (b2 >> 4);
                        int period    = ((b0 & 0x0F) << 8) | b1;
                        int effect    = b2 & 0x0F;
                        int param     = b3;

                        pattern.Cells[row, ch] = new PatternCell
                        {
                            Instrument  = sampleNum,
                            Note        = PeriodToNote(period),
                            Effect      = effect,
                            EffectParam = param
                        };
                    }

                module.Patterns.Add(pattern);
            }

            // ── Durée estimée ─────────────────────────────────────────────────
            module.InitialBpm   = 125;
            module.InitialSpeed = 6;
            double rowsTotal = module.OrderList.Count * 64.0;
            module.DurationSeconds = rowsTotal * module.InitialSpeed / (module.InitialBpm * 0.4);

            return Task.FromResult(module);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string ReadFixedString(BinaryReader br, int length)
        {
            byte[] bytes = br.ReadBytes(length);
            int end = Array.IndexOf(bytes, (byte)0);
            return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end).Trim();
        }

        private static ushort ReadBigEndianUInt16(BinaryReader br)
        {
            byte hi = br.ReadByte();
            byte lo = br.ReadByte();
            return (ushort)((hi << 8) | lo);
        }

        // Table des périodes ProTracker → index de note (1-based, 0 = vide)
        private static readonly int[] PeriodTable =
        [
            1712, 1616, 1525, 1440, 1357, 1281, 1209, 1141, 1077, 1017,  961,  907, // C-1..B-1
             856,  808,  762,  720,  678,  640,  604,  570,  538,  508,  480,  453, // C-2..B-2
             428,  404,  381,  360,  339,  320,  302,  285,  269,  254,  240,  226, // C-3..B-3
             214,  202,  190,  180,  170,  160,  151,  143,  135,  127,  120,  113, // C-4..B-4
             107,  101,   95,   90,   85,   80,   76,   71,   67,   64,   60,   57, // C-5..B-5
        ];

        private static int PeriodToNote(int period)
        {
            if (period == 0) return 0;
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < PeriodTable.Length; i++)
            {
                int dist = Math.Abs(PeriodTable[i] - period);
                if (dist < bestDist) { bestDist = dist; best = i + 1; }
            }
            return best;
        }
    }
}
