using System;
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class Quick
    {
        private static readonly int QBITMASK = 0xff;
        public static ushort quick_text_loc;
        public static ushort Unpack_QUICK(byte[] input, byte[] output, ushort origsize)
        {
            ushort i, j;
            int outputPtr = 0;

            GetBits getBits = new();

            getBits.InitBitBuf(input);

            int outendPtr = origsize;

            while (outputPtr < outendPtr) 
            {
                if (getBits.GETBITS(1) != 0)
                {
                    getBits.DROPBITS(1);
                    output[outputPtr++] = Tables.text[quick_text_loc++ & QBITMASK] = (byte)getBits.GETBITS(8); 
                    getBits.DROPBITS(8);
                }
                else
                {
                    getBits.DROPBITS(1);
                    j = (ushort)(getBits.GETBITS(2) + 2); getBits.DROPBITS(2);
                    i = (ushort)(quick_text_loc - getBits.GETBITS(8) - 1); getBits.DROPBITS(8);
                    while (j--!=0)
                    {
                        output[outputPtr++] = Tables.text[quick_text_loc++ & QBITMASK] = Tables.text[i++ & QBITMASK];
                    }
                }
            }

            quick_text_loc = (ushort)((quick_text_loc + 5) & QBITMASK);

            return 0;
        }
    }
}
