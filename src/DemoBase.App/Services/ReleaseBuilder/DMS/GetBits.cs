#pragma warning disable CS8618
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class GetBits
    {
        private static readonly ulong[] mask_bits =
        {
            0x000000L,0x000001L,0x000003L,0x000007L,0x00000fL,0x00001fL,
            0x00003fL,0x00007fL,0x0000ffL,0x0001ffL,0x0003ffL,0x0007ffL,
            0x000fffL,0x001fffL,0x003fffL,0x007fffL,0x00ffffL,0x01ffffL,
            0x03ffffL,0x07ffffL,0x0fffffL,0x1fffffL,0x3fffffL,0x7fffffL,
            0xffffffL
        };

        private static byte[] inData;
        private static int inDataPtr;
        private static byte bitCount = 0;
        private static ulong bitBuf;

        public void InitBitBuf(byte[] data)
        {
            bitBuf = 0;
            bitCount = 0;
            inData = data;
            inDataPtr = 0;
            DROPBITS(0);
        }

        public ushort GETBITS(int n)
        {
            return (ushort)(bitBuf >> (bitCount - n));
        }

        public void DROPBITS(int n)
        {
            if(inData!=null)
            {
                bitBuf &= mask_bits[bitCount -= (byte)n];
                while (bitCount < 16)
                {
                    bitBuf = (ulong)((bitBuf << 8) | (inData[inDataPtr++]));
                    bitCount += 8;
                }
            }
        }
    }
}
