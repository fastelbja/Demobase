#pragma warning disable CS8618, CS8625, CS0675, CS0168
using DemosceneDownloader.Services;
using System.IO;

namespace DemosceneDownloader.Services.DMS
{
    public class DMS
    {

        private static readonly Logger logger = new("logDMS.txt");

        /* Functions return codes */
        private static readonly ushort NO_PROBLEM = 0;
        private static readonly ushort FILE_END = 1;
        private static readonly ushort ERR_NOTDMS = 5;
        private static readonly ushort ERR_SREAD = 6;
        private static readonly ushort ERR_HCRC = 7;
        private static readonly ushort ERR_NOTTRACK = 8;
        private static readonly ushort ERR_BIGTRACK = 9;
        private static readonly ushort ERR_THCRC = 10;
        private static readonly ushort ERR_TDCRC = 11;
        private static readonly ushort ERR_CSUM = 12;
        private static readonly ushort ERR_BADDECR = 14;
        private static readonly ushort ERR_UNKNMODE = 15;
        private static readonly ushort ERR_NOPASSWD = 16;
        private static readonly ushort ERR_BADPASSWD = 17;
        private static readonly ushort ERR_FMS = 18;
        //private static readonly ushort ERR_GZIP = 19;
        //private static readonly ushort ERR_READDISK = 20;

        private static readonly ushort THLEN = 20;

        private static readonly ushort TRACK_BUFFER_LEN = 32000;

        /* Command to execute */
        private static readonly int CMD_VIEW = 1;
        //private static readonly int CMD_VIEWFULL = 2;
        //private static readonly int CMD_SHOWDIZ = 3;
        private static readonly int CMD_SHOWBANNER = 4;
        //private static readonly int CMD_TEST = 5;
        private static readonly int CMD_UNPACK = 6;
        //private static readonly int CMD_UNPKGZ = 7;
        //private static readonly int CMD_EXTRACT = 8;

        private static readonly int HEADLEN = 56;

        //private static readonly bool OPT_VERBOSE = false;

        // OverrideErrors=true : ignore les erreurs de checksum sur les tracks
        // Beaucoup de vieux DMS ont des checksums incorrects mais sont lisibles
        public static bool OverrideErrors = true;
        private static ushort PWDCRC;

        public static ushort ProcessFile(string iname, string oname, ushort cmd, ushort opt, ushort PCRC, ushort pwd)
        {
            ushort from, to, geninfo, c_version, cmode, hcrc, disktype, pv, ret;
            ulong pkfsize, unpkfsize;
            ulong dateTime;

            byte[] b1 = new byte[TRACK_BUFFER_LEN];
            byte[] b2 = new byte[TRACK_BUFFER_LEN];

            // using garantit la fermeture du fichier même en cas d'exception ou de return anticipé
            using var inputFS = new FileStream(iname, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var inputBR = new BinaryReader(inputFS);

            if(File.Exists(oname)) File.Delete(oname);

            if (inputBR.Read(b1, 0, HEADLEN) != HEADLEN)
                return ERR_SREAD;

            if ((b1[0] != 'D') || (b1[1] != 'M') || (b1[2] != 'S') || (b1[3] != '!'))
                return ERR_NOTDMS;

            hcrc = (ushort)((b1[HEADLEN - 2] << 8) | b1[HEADLEN - 1]);

            if (hcrc != CheckSum.CreateCRC(b1 , 4, (ulong)(HEADLEN - 6)))
                return ERR_HCRC;

            geninfo   = (ushort)((b1[10] << 8) | b1[11]);
            dateTime  = (ulong)(b1[12] << 24) | (ulong)(b1[13] << 16) | (ulong)(b1[14] << 8) | b1[15];
            from      = (ushort)((b1[16] << 8) | b1[17]);
            to        = (ushort)((b1[18] << 8) | b1[19]);
            pkfsize   = (ulong)((((ulong)b1[21]) << 16) | (((ulong)b1[22]) << 8) | b1[23]);
            unpkfsize = (ulong)((((ulong)b1[25]) << 16) | (((ulong)b1[26]) << 8) | b1[27]);
            c_version = (ushort)((b1[46] << 8) | b1[47]);
            disktype  = (ushort)((b1[50] << 8) | b1[51]);
            cmode     = (ushort)((b1[52] << 8) | b1[53]);

            PWDCRC = PCRC;

            if (disktype == 7)
                return ERR_FMS;

            if (((cmd == CMD_UNPACK) || (cmd == CMD_SHOWBANNER)) && ((geninfo & 2)!=0) && (pwd==0))
                return ERR_NOPASSWD;

            ret = NO_PROBLEM;

            Tables.Init_Decrunchers();

            using var outputFS = new FileStream(oname, FileMode.Create, FileAccess.Write);
            using var outputWR = new BinaryWriter(outputFS);

            if (cmd != CMD_VIEW)
            {
                if (cmd == CMD_SHOWBANNER)
                    ret = Process_Track(inputBR, null, b1, b2, cmd, opt, (ushort)((geninfo & 2)!=0 ? pwd : 0));
                else
                    while ((ret = Process_Track(inputBR, outputWR, b1, b2, cmd, opt,(ushort)((geninfo & 2) !=0 ? pwd : 0))) == NO_PROBLEM) ;
            }

            if (ret == FILE_END)     ret = NO_PROBLEM;
            if (ret == ERR_NOTTRACK) ret = NO_PROBLEM;

            return ret;
            // inputBR, inputFS, outputWR, outputFS fermés automatiquement par using
        }

        private static ushort Process_Track(BinaryReader fileIn, BinaryWriter fileOut, byte[] b1, byte[] b2, ushort cmd, ushort opt, ushort pwd)
        {
            ushort hcrc, dcrc, usum, number, pklen1, pklen2, unpklen, l, r;
            byte cmode, flags;


            l = (ushort)fileIn.Read(b1, 0, THLEN);

            if (l != THLEN)
            {
                if (l == 0)
                    return FILE_END;
                else
                    return ERR_SREAD;
            }

            /*  "TR" identifies a Track Header  */
            if ((b1[0] != 'T') || (b1[1] != 'R')) return ERR_NOTTRACK;

            /*  Track Header CRC  */
            hcrc = (ushort)((b1[THLEN - 2] << 8) | b1[THLEN - 1]);

            if (CheckSum.CreateCRC(b1, 0, (ulong)(THLEN - 2)) != hcrc)
                return ERR_THCRC;

            number = (ushort)((b1[2] << 8) | b1[3]);    /*  Number of track  */
            pklen1 = (ushort)((b1[6] << 8) | b1[7]);    /*  Length of packed track data as in archive  */
            pklen2 = (ushort)((b1[8] << 8) | b1[9]);    /*  Length of data after first unpacking  */
            unpklen = (ushort)((b1[10] << 8) | b1[11]); /*  Length of data after subsequent rle unpacking */
            flags = b1[12];     /*  control flags  */
            cmode = b1[13];     /*  compression mode used  */
            usum = (ushort)((b1[14] << 8) | b1[15]);    /*  Track Data CheckSum AFTER unpacking  */
            dcrc = (ushort)((b1[16] << 8) | b1[17]);    /*  Track Data CRC BEFORE unpacking  */

            if ((pklen1 > TRACK_BUFFER_LEN) || (pklen2 > TRACK_BUFFER_LEN) || (unpklen > TRACK_BUFFER_LEN)) return ERR_BIGTRACK;

            if (fileIn.Read(b1, 0, pklen1) != pklen1) return ERR_SREAD;

            if (CheckSum.CreateCRC(b1, 0, (ulong)pklen1) != dcrc)
            {
                if (OverrideErrors)
                {
                    logger.Error("Detected a CRC error on track " + number.ToString() +" but overriding.");
                }
                else
                {
                    return ERR_TDCRC;
                }
            }

            /*  track 80 is FILEID.DIZ, track 0xffff (-1) is Banner  */
            /*  and track 0 with 1024 bytes only is a fake boot block with more advertising */
            /*  FILE_ID.DIZ is never encrypted  */

            if ((pwd != 0) && (number != 80))
                DMS_Decrypt(b1, pklen1);

            if ((cmd == CMD_UNPACK) && (number < 80) && (unpklen > 2048))
            {
                Tables.ForMemset(b2, 0, 0, unpklen);

                r = Unpack_Track(b1, b2, pklen2, unpklen, cmode, flags);
                if (r != NO_PROBLEM)
                {
                    if (OverrideErrors)
                    {
                        logger.Error("Detected an error while unpacking track " + number.ToString() +", but overriding.");
                    }
                    else
                    {
                        if (pwd!=0)
                            return ERR_BADPASSWD;
                        else
                            return r;
                    }
                }
                if (usum != CheckSum.Calc_CheckSum(b2, (ulong)unpklen))
                {
                    if (OverrideErrors)
                    {
                        logger.Error("Detected an error after unpacking track "+ number.ToString() +", but overriding.");
                    }
                    else
                    {
                        if (pwd!=0)
                            return ERR_BADPASSWD;
                        else
                            return ERR_CSUM;
                    }
                }

                fileOut.Write(b2, 0, unpklen);
            }

            return NO_PROBLEM;
        }

        private static ushort Unpack_Track(byte[] b1, byte[] b2, ushort pklen2, ushort unpklen, byte cmode, byte flags)
        {
            switch (cmode)
            {
                case 0:
                    /*   No Compression   */
                    Array.Copy(b1, 0, b2, 0, unpklen);
                    break;
                case 1:
                    /*   Simple Compression   */
                    if (RLE.Unpack_RLE(b1, b2, unpklen) != 0) return ERR_BADDECR;
                    break;
                case 2:
                    /*   Quick Compression   */
                    if (Quick.Unpack_QUICK(b1, b2, pklen2) != 0) return ERR_BADDECR;
                    if (RLE.Unpack_RLE(b2, b1, unpklen) != 0) return ERR_BADDECR;
                    Array.Copy(b1, 0, b2, 0, unpklen);
                    break;
                case 3:
                    /*   Medium Compression   */
                    if (Medium.Unpack_MEDIUM(b1, b2, pklen2) != 0) return ERR_BADDECR;
                    if (RLE.Unpack_RLE(b2, b1, unpklen) != 0) return ERR_BADDECR;
                    Array.Copy(b1, 0, b2, 0, unpklen);
                    break;
                case 4:
                    /*   Deep Compression   */
                    if (Deep.Unpack_DEEP(b1, b2, pklen2)!=0) return ERR_BADDECR;
                    if (RLE.Unpack_RLE(b2, b1, unpklen) != 0) return ERR_BADDECR;
                    Array.Copy(b1, 0, b2, 0, unpklen);
                    break;
                case 5:
                case 6:
                    /*   Heavy Compression   */
                    if (cmode == 5)
                    {
                        /*   Heavy 1   */
                        if (Heavy.Unpack_HEAVY(b1, b2, (byte)(flags & 7), pklen2)!=0) return ERR_BADDECR;
                    }
                    else
                    {
                        /*   Heavy 2   */
                        if (Heavy.Unpack_HEAVY(b1, b2, (byte)(flags | 8), pklen2)!=0) return ERR_BADDECR;
                    }
                    if ((flags & 4) != 0)
                    {
                        /*  Unpack with RLE only if this flag is set  */
                        if (RLE.Unpack_RLE(b2, b1, unpklen) != 0) return ERR_BADDECR;
                        Array.Copy(b1, 0, b2, 0, unpklen);
                    }
                    break;
                default:
                    return ERR_UNKNMODE;
            }

            if ((flags & 1) == 0) Tables.Init_Decrunchers();

            return NO_PROBLEM;
        }

        /*  DMS uses a lame encryption  */
        static void DMS_Decrypt(byte[] p, ushort len)
        {
            ushort t;
            int ptr = 0;

            while (len--!=0)
            {
                t = (ushort) p[ptr];
                p[ptr++] ^= (byte)PWDCRC;
                PWDCRC = (ushort)((PWDCRC >> 1) + t);
            }
        }
    }
}
