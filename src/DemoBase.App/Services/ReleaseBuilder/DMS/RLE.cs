using System;
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class RLE
    {
        public static ushort Unpack_RLE(byte[] input, byte[] output, ushort origsize)
        {
            ushort n;
            byte a, b;
            int outputEnd;
            int outputPtr = 0;
            int inputPtr = 0;
            outputEnd = origsize;
            
            while (outputPtr< outputEnd)
            {
                if ((a = input[inputPtr++]) != 0x90)
                    output[outputPtr++] = a;

                else if ((b = input[inputPtr++])==0)
                    output[outputPtr++] = a;

                else
                {
                    a = input[inputPtr++];
                    if (b == 0xff)
                    {
                        n = input[inputPtr++];
                        n = (ushort)((n << 8) + input[inputPtr++]);
                    }
                    else
                        n = b;
                    if (outputPtr +n > outputEnd) return 1;
                    
                    Tables.ForMemset(output, outputPtr, a, n);
			        outputPtr += n;
                }
            }
            return 0;
        }

    }
}
