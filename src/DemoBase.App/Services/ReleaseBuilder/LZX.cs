using System;
using System.Collections.Generic;
using System.IO;


using LONG = System.Int32;      /* 32 bits (or more) */
using UBYTE = System.Byte;      /* 8 bits exactly    */
using ULONG = System.UInt32;    /* 32 bits (or more) */
using UWORD = System.UInt16;    /* 16 bits (or more) */

namespace DemosceneDownloader.Services
{
    public sealed class LZX
    {
        FileStream? fs;
        BinaryReader? br;

        LONG pack_size;
        LONG unpack_size;

        ULONG crc;
        UBYTE pack_mode;

        readonly UBYTE[] read_buffer = new UBYTE[16384];                 /* have a reasonable sized read buffer */
        readonly UBYTE[] decrunch_buffer = new UBYTE[258 + 65536 + 258]; /* allow overrun for speed */

        LONG sourcePtr;
        LONG destinationPtr;
        LONG source_endPtr;
        LONG destination_endPtr;

        ULONG decrunch_method;
        LONG decrunch_length;
        LONG last_offset;
        ULONG global_control;
        LONG global_shift;

        UBYTE[] offset_len = new UBYTE[8];
        UWORD[] offset_table = new UWORD[128];
        UBYTE[] huffman20_len = new UBYTE[20];
        UWORD[] huffman20_table = new UWORD[96];
        UBYTE[] literal_len = new UBYTE[768];
        UWORD[] literal_table = new UWORD[5120];

        public class FilenameNode
        {
            public LONG packedlength;
            public LONG unpackedLength;
            public ULONG crc;
            public string filename;
            public int filenameSize;
            public FilenameNode(LONG packedLength, LONG unpackLength, ULONG crc, string filename, int filenameSize)
            {
                this.packedlength = packedLength;
                this.unpackedLength = unpackLength;
                this.crc = crc;
                this.filename = filename;
                this.filenameSize = filenameSize;
            }
        };

        ULONG sum;

        readonly ULONG[] crc_table = new ULONG[256]
        {
             0x00000000,0x77073096,0xEE0E612C,0x990951BA,0x076DC419,0x706AF48F,
             0xE963A535,0x9E6495A3,0x0EDB8832,0x79DCB8A4,0xE0D5E91E,0x97D2D988,
             0x09B64C2B,0x7EB17CBD,0xE7B82D07,0x90BF1D91,0x1DB71064,0x6AB020F2,
             0xF3B97148,0x84BE41DE,0x1ADAD47D,0x6DDDE4EB,0xF4D4B551,0x83D385C7,
             0x136C9856,0x646BA8C0,0xFD62F97A,0x8A65C9EC,0x14015C4F,0x63066CD9,
             0xFA0F3D63,0x8D080DF5,0x3B6E20C8,0x4C69105E,0xD56041E4,0xA2677172,
             0x3C03E4D1,0x4B04D447,0xD20D85FD,0xA50AB56B,0x35B5A8FA,0x42B2986C,
             0xDBBBC9D6,0xACBCF940,0x32D86CE3,0x45DF5C75,0xDCD60DCF,0xABD13D59,
             0x26D930AC,0x51DE003A,0xC8D75180,0xBFD06116,0x21B4F4B5,0x56B3C423,
             0xCFBA9599,0xB8BDA50F,0x2802B89E,0x5F058808,0xC60CD9B2,0xB10BE924,
             0x2F6F7C87,0x58684C11,0xC1611DAB,0xB6662D3D,0x76DC4190,0x01DB7106,
             0x98D220BC,0xEFD5102A,0x71B18589,0x06B6B51F,0x9FBFE4A5,0xE8B8D433,
             0x7807C9A2,0x0F00F934,0x9609A88E,0xE10E9818,0x7F6A0DBB,0x086D3D2D,
             0x91646C97,0xE6635C01,0x6B6B51F4,0x1C6C6162,0x856530D8,0xF262004E,
             0x6C0695ED,0x1B01A57B,0x8208F4C1,0xF50FC457,0x65B0D9C6,0x12B7E950,
             0x8BBEB8EA,0xFCB9887C,0x62DD1DDF,0x15DA2D49,0x8CD37CF3,0xFBD44C65,
             0x4DB26158,0x3AB551CE,0xA3BC0074,0xD4BB30E2,0x4ADFA541,0x3DD895D7,
             0xA4D1C46D,0xD3D6F4FB,0x4369E96A,0x346ED9FC,0xAD678846,0xDA60B8D0,
             0x44042D73,0x33031DE5,0xAA0A4C5F,0xDD0D7CC9,0x5005713C,0x270241AA,
             0xBE0B1010,0xC90C2086,0x5768B525,0x206F85B3,0xB966D409,0xCE61E49F,
             0x5EDEF90E,0x29D9C998,0xB0D09822,0xC7D7A8B4,0x59B33D17,0x2EB40D81,
             0xB7BD5C3B,0xC0BA6CAD,0xEDB88320,0x9ABFB3B6,0x03B6E20C,0x74B1D29A,
             0xEAD54739,0x9DD277AF,0x04DB2615,0x73DC1683,0xE3630B12,0x94643B84,
             0x0D6D6A3E,0x7A6A5AA8,0xE40ECF0B,0x9309FF9D,0x0A00AE27,0x7D079EB1,
             0xF00F9344,0x8708A3D2,0x1E01F268,0x6906C2FE,0xF762575D,0x806567CB,
             0x196C3671,0x6E6B06E7,0xFED41B76,0x89D32BE0,0x10DA7A5A,0x67DD4ACC,
             0xF9B9DF6F,0x8EBEEFF9,0x17B7BE43,0x60B08ED5,0xD6D6A3E8,0xA1D1937E,
             0x38D8C2C4,0x4FDFF252,0xD1BB67F1,0xA6BC5767,0x3FB506DD,0x48B2364B,
             0xD80D2BDA,0xAF0A1B4C,0x36034AF6,0x41047A60,0xDF60EFC3,0xA867DF55,
             0x316E8EEF,0x4669BE79,0xCB61B38C,0xBC66831A,0x256FD2A0,0x5268E236,
             0xCC0C7795,0xBB0B4703,0x220216B9,0x5505262F,0xC5BA3BBE,0xB2BD0B28,
             0x2BB45A92,0x5CB36A04,0xC2D7FFA7,0xB5D0CF31,0x2CD99E8B,0x5BDEAE1D,
             0x9B64C2B0,0xEC63F226,0x756AA39C,0x026D930A,0x9C0906A9,0xEB0E363F,
             0x72076785,0x05005713,0x95BF4A82,0xE2B87A14,0x7BB12BAE,0x0CB61B38,
             0x92D28E9B,0xE5D5BE0D,0x7CDCEFB7,0x0BDBDF21,0x86D3D2D4,0xF1D4E242,
             0x68DDB3F8,0x1FDA836E,0x81BE16CD,0xF6B9265B,0x6FB077E1,0x18B74777,
             0x88085AE6,0xFF0F6A70,0x66063BCA,0x11010B5C,0x8F659EFF,0xF862AE69,
             0x616BFFD3,0x166CCF45,0xA00AE278,0xD70DD2EE,0x4E048354,0x3903B3C2,
             0xA7672661,0xD06016F7,0x4969474D,0x3E6E77DB,0xAED16A4A,0xD9D65ADC,
             0x40DF0B66,0x37D83BF0,0xA9BCAE53,0xDEBB9EC5,0x47B2CF7F,0x30B5FFE9,
             0xBDBDF21C,0xCABAC28A,0x53B39330,0x24B4A3A6,0xBAD03605,0xCDD70693,
             0x54DE5729,0x23D967BF,0xB3667A2E,0xC4614AB8,0x5D681B02,0x2A6F2B94,
             0xB40BBE37,0xC30C8EA1,0x5A05DF1B,0x2D02EF8D
        };

        readonly UBYTE[] table_one = new UBYTE[32]
        {
            0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13,14,14
        };

        readonly LONG[] table_two = new LONG[32]
        {
             0,1,2,3,4,6,8,12,16,24,32,48,64,96,128,192,256,384,512,768,1024,
             1536,2048,3072,4096,6144,8192,12288,16384,24576,32768,49152
        };

        readonly LONG[] table_three = new LONG[16]
        {
            0,1,3,7,15,31,63,127,255,511,1023,2047,4095,8191,16383,32767
        };

        readonly UBYTE[] table_four = new UBYTE[34]
        {
             0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,
             0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16
        };

        private string destination_folder = string.Empty;
        public string DestinationFolder
        {
            get
            {
                return this.destination_folder;
            }
            set
            {
                string folderTest = value;
                if (!folderTest.EndsWith("\\") && !folderTest.EndsWith("/"))
                    folderTest += "\\";
                this.destination_folder = folderTest;
            }
        }

        private void CRC_Calc(UBYTE[] memory, int memoryPtr, LONG length)
        {
            ULONG temp;

            if (length > 0)
            {
                temp = ~sum; /* was (sum ^ 4294967295) */
                do
                {
                    temp = crc_table[(memory[memoryPtr++] ^ temp) & 255] ^ (temp >> 8);
                } while (--length != 0);
                sum = ~temp; /* was (temp ^ 4294967295) */
            }
        }

        /* Build a fast huffman decode table from the symbol bit lengths.         */
        /* There is an alternate algorithm which is faster but also more complex. */
        private static int MakeDecodeTable(int number_symbols, LONG table_size, ref UBYTE[] length, ref UWORD[] table)
        {
            UBYTE bit_num = 0;
            LONG symbol;
            LONG pos;
            LONG leaf; /* could be a register */
            LONG table_mask, bit_mask, fill, next_symbol, reverse;
            int abort = 0;

            pos = 0; /* consistantly used as the current position in the decode table */

            bit_mask = table_mask = (1 << table_size);

            bit_mask >>= 1; /* don't do the first number */
            bit_num++;

            while ((abort == 0) && (bit_num <= table_size))
            {
                for (symbol = 0; symbol < number_symbols; symbol++)
                {
                    if (length[symbol] == bit_num)
                    {
                        reverse = pos; /* reverse the order of the position's bits */
                        leaf = 0;
                        fill = table_size;
                        do /* reverse the position */
                        {
                            leaf = (leaf << 1) + (reverse & 1);
                            reverse >>= 1;
                        } while (--fill != 0);
                        if ((pos += bit_mask) > table_mask)
                        {
                            abort = 1;
                            break; /* we will overrun the table! abort! */
                        }
                        fill = bit_mask;
                        next_symbol = (1 << bit_num);
                        do
                        {
                            table[leaf] = (UWORD)symbol;
                            leaf += next_symbol;
                        } while (--fill != 0);
                    }
                }
                bit_mask >>= 1;
                bit_num++;
            }

            if ((abort == 0) && (pos != table_mask))
            {
                for (symbol = pos; symbol < table_mask; symbol++) /* clear the rest of the table */
                {
                    reverse = symbol; /* reverse the order of the position's bits */
                    leaf = 0;
                    fill = table_size;
                    do /* reverse the position */
                    {
                        leaf = (leaf << 1) + (reverse & 1);
                        reverse >>= 1;
                    } while (--fill != 0);
                    table[leaf] = 0;
                }
                next_symbol = table_mask >> 1;
                pos <<= 16;
                table_mask <<= 16;
                bit_mask = 32768;

                while ((abort == 0) && (bit_num <= 16))
                {
                    for (symbol = 0; symbol < number_symbols; symbol++)
                    {
                        if (length[symbol] == bit_num)
                        {
                            reverse = pos >> 16; /* reverse the order of the position's bits */
                            leaf = 0;
                            fill = table_size;
                            do /* reverse the position */
                            {
                                leaf = (leaf << 1) + (reverse & 1);
                                reverse >>= 1;
                            } while (--fill != 0);
                            for (fill = 0; fill < bit_num - table_size; fill++)
                            {
                                if (table[leaf] == 0)
                                {
                                    table[(next_symbol << 1)] = 0;
                                    table[(next_symbol << 1) + 1] = 0;
                                    table[leaf] = (UWORD)next_symbol++;
                                }
                                leaf = table[leaf] << 1;
                                leaf += (pos >> (int)(15 - fill)) & 1;
                            }
                            table[leaf] = (UWORD)symbol;
                            if ((pos += bit_mask) > table_mask)
                            {
                                abort = 1;
                                break; /* we will overrun the table! abort! */
                            }
                        }
                    }
                    bit_mask >>= 1;
                    bit_num++;
                }
            }
            if (pos != table_mask) abort = 1; /* the table is incomplete! */

            return (abort);
        }

        /* ---------------------------------------------------------------------- */

        /* Read and build the decrunch tables. There better be enough data in the */
        /* source buffer or it's stuffed. */
        private int ReadLiteralTable()
        {
            ULONG control;
            LONG shift;
            ULONG temp; /* could be a register */
            ULONG symbol, pos, count, fix, max_symbol;
            int abort = 0;

            control = global_control;
            shift = global_shift;

            if (shift < 0) /* fix the control word if necessary */
            {
                shift += 16;
                control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                control += (ULONG)(read_buffer[sourcePtr++] << shift);
            }

            /* read the decrunch method */

            decrunch_method = control & 7;
            control >>= 3;
            if ((shift -= 3) < 0)
            {
                shift += 16;
                control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                control += (ULONG)(read_buffer[sourcePtr++] << shift);
            }

            /* Read and build the offset huffman table */

            if ((abort == 0) && (decrunch_method == 3))
            {
                for (temp = 0; temp < 8; temp++)
                {
                    offset_len[temp] = (UBYTE)(control & 7);
                    control >>= 3;
                    if ((shift -= 3) < 0)
                    {
                        shift += 16;
                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                    }
                }
                abort = MakeDecodeTable(8, 7, ref offset_len, ref offset_table);
            }

            /* read decrunch length */

            if (abort == 0)
            {
                decrunch_length = (LONG)((control & 255) << 16);
                control >>= 8;
                if ((shift -= 8) < 0)
                {
                    shift += 16;
                    control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                    control += (ULONG)(read_buffer[sourcePtr++] << shift);
                }
                decrunch_length += (LONG)((control & 255) << 8);
                control >>= 8;
                if ((shift -= 8) < 0)
                {
                    shift += 16;
                    control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                    control += (ULONG)(read_buffer[sourcePtr++] << shift);
                }
                decrunch_length += (LONG)((control & 255));
                control >>= 8;
                if ((shift -= 8) < 0)
                {
                    shift += 16;
                    control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                    control += (ULONG)(read_buffer[sourcePtr++] << shift);
                }
            }

            /* read and build the huffman literal table */

            if ((abort == 0) && (decrunch_method != 1))
            {
                pos = 0;
                fix = 1;
                max_symbol = 256;

                do
                {
                    for (temp = 0; temp < 20; temp++)
                    {
                        huffman20_len[temp] = (UBYTE)(control & 15);
                        control >>= 4;
                        if ((shift -= 4) < 0)
                        {
                            shift += 16;
                            control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                            control += (ULONG)(read_buffer[sourcePtr++] << shift);
                        }
                    }
                    abort = MakeDecodeTable(20, 6, ref huffman20_len, ref huffman20_table);

                    if (abort != 0) break; /* argh! table is corrupt! */

                    do
                    {
                        if ((symbol = huffman20_table[control & 63]) >= 20)
                        {
                            do /* symbol is longer than 6 bits */
                            {
                                symbol = huffman20_table[((control >> 6) & 1) + (symbol << 1)];
                                if (shift-- == 0)
                                {
                                    shift += 16;
                                    control += (ULONG)(read_buffer[sourcePtr++] << 24);
                                    control += (ULONG)(read_buffer[sourcePtr++] << 16);
                                }
                                control >>= 1;
                            } while (symbol >= 20);
                            temp = 6;
                        }
                        else
                        {
                            temp = huffman20_len[symbol];
                        }
                        control >>= (LONG)temp;
                        if ((shift -= (LONG)temp) < 0)
                        {
                            shift += 16;
                            control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                            control += (ULONG)(read_buffer[sourcePtr++] << shift);
                        }
                        switch (symbol)
                        {
                            case 17:
                            case 18:
                                {
                                    if (symbol == 17)
                                    {
                                        temp = 4;
                                        count = 3;
                                    }
                                    else /* symbol == 18 */
                                    {
                                        temp = 6 - fix;
                                        count = 19;
                                    }
                                    count += (ULONG)((control & table_three[temp]) + fix);
                                    control >>= (LONG)temp;
                                    if ((shift -= (LONG)temp) < 0)
                                    {
                                        shift += 16;
                                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                                    }
                                    while ((pos < max_symbol) && ((count--) != 0))
                                        literal_len[pos++] = 0;
                                    break;
                                }
                            case 19:
                                {
                                    count = (control & 1) + 3 + fix;
                                    if (shift-- == 0)
                                    {
                                        shift += 16;
                                        control += (ULONG)(read_buffer[sourcePtr++] << 24);
                                        control += (ULONG)(read_buffer[sourcePtr++] << 16);
                                    }
                                    control >>= 1;
                                    if ((symbol = huffman20_table[control & 63]) >= 20)
                                    {
                                        do /* symbol is longer than 6 bits */
                                        {
                                            symbol = huffman20_table[((control >> 6) & 1) + (symbol << 1)];
                                            if (shift-- == 0)
                                            {
                                                shift += 16;
                                                control += (ULONG)(read_buffer[sourcePtr++] << 24);
                                                control += (ULONG)(read_buffer[sourcePtr++] << 16);
                                            }
                                            control >>= 1;
                                        } while (symbol >= 20);
                                        temp = 6;
                                    }
                                    else
                                    {
                                        temp = huffman20_len[symbol];
                                    }
                                    control >>= (LONG)temp;
                                    if ((shift -= (LONG)temp) < 0)
                                    {
                                        shift += 16;
                                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                                    }
                                    symbol = table_four[literal_len[pos] + 17 - symbol];
                                    while ((pos < max_symbol) && ((count--) != 0))
                                        literal_len[pos++] = (UBYTE)symbol;
                                    break;
                                }
                            default:
                                {
                                    symbol = table_four[literal_len[pos] + 17 - symbol];
                                    literal_len[pos++] = (UBYTE)symbol;
                                    break;
                                }
                        }
                    } while (pos < max_symbol);
                    fix--;
                    max_symbol += 512;
                } while (max_symbol == 768);

                if (abort == 0)
                    abort = MakeDecodeTable(768, 12, ref literal_len, ref literal_table);
            }

            global_control = control;
            global_shift = shift;

            return (abort);
        }

        /* ---------------------------------------------------------------------- */
        /* Fill up the decrunch buffer. Needs lots of overrun for both destination */
        /* and source buffers. Most of the time is spent in this routine so it's  */
        /* pretty damn optimized. */
        private void Decrunch()
        {
            ULONG control;
            LONG shift;
            LONG temp; /* could be a register */
            LONG symbol, count;
            long bufStringPtr;

            control = global_control;
            shift = global_shift;

            do
            {
                if ((symbol = literal_table[control & 4095]) >= 768)
                {
                    control >>= 12;
                    if ((shift -= 12) < 0)
                    {
                        shift += 16;
                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                    }
                    do /* literal is longer than 12 bits */
                    {
                        symbol = literal_table[(control & 1) + (symbol << 1)];
                        if (shift-- == 0)
                        {
                            shift += 16;
                            control += (ULONG)(read_buffer[sourcePtr++] << 24);
                            control += (ULONG)(read_buffer[sourcePtr++] << 16);
                        }
                        control >>= 1;
                    } while (symbol >= 768);
                }
                else
                {
                    temp = literal_len[symbol];
                    control >>= temp;
                    if ((shift -= temp) < 0)
                    {
                        shift += 16;
                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                    }
                }
                if (symbol < 256)
                {
                    decrunch_buffer[destinationPtr++] = (UBYTE)symbol;
                }
                else
                {
                    symbol -= 256;
                    count = table_two[temp = symbol & 31];
                    temp = table_one[temp];
                    if ((temp >= 3) && (decrunch_method == 3))
                    {
                        temp -= 3;
                        count += (LONG)(((control & table_three[temp]) << 3));
                        control >>= temp;
                        if ((shift -= temp) < 0)
                        {
                            shift += 16;
                            control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                            control += (ULONG)(read_buffer[sourcePtr++] << shift);
                        }
                        count += (temp = offset_table[control & 127]);
                        temp = offset_len[temp];
                    }
                    else
                    {
                        count += (LONG)(control & table_three[temp]);
                        if (count == 0) count = last_offset;
                    }
                    control >>= temp;
                    if ((shift -= temp) < 0)
                    {
                        shift += 16;
                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                    }
                    last_offset = count;

                    count = table_two[temp = (symbol >> 5) & 15] + 3;
                    temp = table_one[temp];
                    count += (LONG)((control & table_three[temp]));
                    control >>= temp;
                    if ((shift -= temp) < 0)
                    {
                        shift += 16;
                        control += (ULONG)(read_buffer[sourcePtr++] << (8 + shift));
                        control += (ULONG)(read_buffer[sourcePtr++] << shift);
                    }

                    if (last_offset < destinationPtr)
                        bufStringPtr = destinationPtr - last_offset;
                    else
                        bufStringPtr = destinationPtr + 65536 - last_offset;

                    do
                    {
                        decrunch_buffer[destinationPtr++] = decrunch_buffer[bufStringPtr++];
                    } while (--count != 0);
                }
            } while ((destinationPtr < destination_endPtr) && (sourcePtr < source_endPtr));

            global_control = control;
            global_shift = shift;
        }


        /* ---------------------------------------------------------------------- */

        /* Trying to understand this function is hazardous. */
        public int ExtractNormal(List<FilenameNode> fn)
        {
            LONG posPtr;
            LONG count;
            LONG tempPtr;

            int abort = 0;

            global_control = 0; /* initial control word */
            global_shift = -16;
            last_offset = 1;
            unpack_size = 0;
            decrunch_length = 0;

            for (count = 0; count < 8; count++)
                offset_len[count] = 0;

            for (count = 0; count < 768; count++)
                literal_len[count] = 0;

            sourcePtr = 16384;
            source_endPtr = 16384 - 1024;
            posPtr = destination_endPtr = destinationPtr = 258 + 65536;


            foreach (FilenameNode filename_Node in fn)
            {
                sum = 0; /* reset CRC */

                unpack_size = filename_Node.unpackedLength;
                string fileNameToSave = CheckForInvalidWindowsName(filename_Node.filename[..filename_Node.filenameSize]);
                // Protection path traversal AVANT Directory.CreateDirectory
                string _safeFileName = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination_folder, fileNameToSave));
                if (!_safeFileName.StartsWith(System.IO.Path.GetFullPath(destination_folder), StringComparison.OrdinalIgnoreCase))
                    fileNameToSave = System.IO.Path.GetFileName(fileNameToSave); // sort de tmp → n'utilise que le nom
                string folder = destination_folder + System.IO.Path.GetDirectoryName(fileNameToSave);
                if (folder != null)
                    System.IO.Directory.CreateDirectory(folder);

                FileStream? fs = null;
                BinaryWriter? bw = null;

                if ((!fileNameToSave.EndsWith("/.")) && (unpack_size!=0))
                {
                    if (File.Exists(fileNameToSave))
                        File.Delete(fileNameToSave);

                    string _safePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination_folder, fileNameToSave));
                    if (!_safePath.StartsWith(System.IO.Path.GetFullPath(destination_folder), StringComparison.OrdinalIgnoreCase))
                        _safePath = System.IO.Path.Combine(destination_folder, System.IO.Path.GetFileName(fileNameToSave));
                    if (File.Exists(_safePath)) File.Delete(_safePath);
                    fs = new FileStream(_safePath, FileMode.Create);
                    bw = new BinaryWriter(fs);
                }

                while (unpack_size > 0)
                {

                    if (posPtr == destinationPtr) /* time to fill the buffer? */
                    {
                        /* check if we have enough data and read some if not */
                        if (sourcePtr >= source_endPtr) /* have we exhausted the current read buffer? */
                        {
                            tempPtr = 0;
                            if ((count = -sourcePtr + 16384) != 0)
                            {
                                do /* copy the remaining overrun to the start of the buffer */
                                {
                                    read_buffer[tempPtr++] = read_buffer[sourcePtr++];
                                } while (--count != 0);
                            }

                            sourcePtr = 0;
                            count = sourcePtr - tempPtr + 16384;

                            if (count <= 0) break; /* overflow ou données corrompues */

                            if (pack_size < count) count = pack_size; /* make sure we don't read too much */

                            br?.Read(read_buffer, tempPtr, count);

                            pack_size -= count;

                            tempPtr += count;

                            if (sourcePtr >= tempPtr) break; /* argh! no more data! */
                        } /* if(source >= source_end) */

                        /* check if we need to read the tables */
                        if (decrunch_length <= 0)
                        {
                            if (ReadLiteralTable() != 0) break; /* argh! can't make huffman tables! */
                        }

                        /* unpack some data */
                        if (destinationPtr >= 258 + 65536)
                        {
                            if ((count = (destinationPtr - 65536)) >= 0)
                            {
                                tempPtr = 65536;
                                destinationPtr = 0;
                                do /* copy the overrun to the start of the buffer */
                                {
                                    decrunch_buffer[destinationPtr++] = decrunch_buffer[tempPtr++];
                                } while (--count != 0);
                            }
                            posPtr = destinationPtr;
                        }
                        destination_endPtr = destinationPtr + decrunch_length;
                        if (destination_endPtr > 258 + 65536)
                            destination_endPtr = 258 + 65536;
                        tempPtr = destinationPtr;

                        Decrunch();

                        decrunch_length -= (destinationPtr - tempPtr);
                    }

                    /* calculate amount of data we can use before we need to fill the buffer again */
                    count = destinationPtr - posPtr;
                    if (count > unpack_size) count = unpack_size; /* take only what we need */
                    bw?.Write(decrunch_buffer, posPtr, count);
                    CRC_Calc(decrunch_buffer, posPtr, count);

                    unpack_size -= count;
                    posPtr += count;
                }
                bw?.Close();
                fs?.Close();
            }
            return (abort);
        }

        public static string CheckForInvalidWindowsName(string fileName)
        {
            char substituteChar = '_';

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                if ((invalidChar != 0x2F) && (invalidChar != 0x5c))
                    fileName = fileName.Replace(invalidChar, substituteChar);
            }

            return fileName;
        }
        int ExtractStore(List<FilenameNode> fn)
        {
            LONG count;
            int abort = 0;

            foreach (FilenameNode filename_Node in fn)
            {
                unpack_size = filename_Node.unpackedLength;
                string fileNameToSave = CheckForInvalidWindowsName(filename_Node.filename[..filename_Node.filenameSize]);
                // Protection path traversal AVANT Directory.CreateDirectory
                string _safeFileName = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination_folder, fileNameToSave));
                if (!_safeFileName.StartsWith(System.IO.Path.GetFullPath(destination_folder), StringComparison.OrdinalIgnoreCase))
                    fileNameToSave = System.IO.Path.GetFileName(fileNameToSave); // sort de tmp → n'utilise que le nom
                string folder = destination_folder + System.IO.Path.GetDirectoryName(fileNameToSave);
                if (folder != null)
                    System.IO.Directory.CreateDirectory(folder);

                FileStream? fs = null;
                BinaryWriter? bw = null;

                if (!fileNameToSave.EndsWith("/."))
                {
                    if (File.Exists(fileNameToSave))
                        File.Delete(fileNameToSave);

                    string _safePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination_folder, fileNameToSave));
                    if (!_safePath.StartsWith(System.IO.Path.GetFullPath(destination_folder), StringComparison.OrdinalIgnoreCase))
                        _safePath = System.IO.Path.Combine(destination_folder, System.IO.Path.GetFileName(fileNameToSave));
                    if (File.Exists(_safePath)) File.Delete(_safePath);
                    fs = new FileStream(_safePath, FileMode.Create);
                    bw = new BinaryWriter(fs);
                }
                sum = 0; /* reset CRC */

                if (unpack_size > pack_size) unpack_size = pack_size;

                while (unpack_size > 0)
                {
                    count = ((unpack_size > 16384) ? 16384 : unpack_size);
                    if(br!=null)
                    {
                        if (br.Read(read_buffer, 0, count) != count)
                        {
                            abort = 1;
                            break;
                        }
                    }

                    pack_size -= count;
                    CRC_Calc(read_buffer, 0, count);

                    bw?.Write(read_buffer, 0, count);
                    unpack_size -= count;
                }

                bw?.Close();
                fs?.Close();
            }

            return (abort);
        }

        public int ExtractArchive(string filePath)
        {
            List<FilenameNode> fn = new();
            int actual;
            int abort;

            byte[] archive_header = new byte[31];
            byte[] header_filename = new byte[256];
            byte[] header_comment = new byte[256];

            fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            br = new BinaryReader(fs);

            try
            {
            br.Read(archive_header, 0, 10);

            do
            {
                abort = 1; /* assume an error */
                actual = br.Read(archive_header, 0, 31);
                if (actual != 0)
                {
                    if (actual == 31) /* 0 is normal and means EOF */
                    {

                        sum = 0; /* reset CRC */
                        crc = (ULONG)((archive_header[29] << 24) + (archive_header[28] << 16) + (archive_header[27] << 8) + archive_header[26]); /* header crc */
                        archive_header[29] = 0; /* Must set the field to 0 before calculating the crc */
                        archive_header[28] = 0;
                        archive_header[27] = 0;
                        archive_header[26] = 0;
                        CRC_Calc(archive_header, 0, 31);
                        int filenameSize = archive_header[30]; /* filename length */
                        actual = br.Read(header_filename, 0, filenameSize);
                        if (actual == filenameSize)
                        {
                            header_filename[filenameSize] = 0;
                            CRC_Calc(header_filename, 0, filenameSize);
                            int commentLength = archive_header[14]; /* comment length */

                            string filename = System.Text.Encoding.Default.GetString(header_filename);

                            actual = br.Read(header_comment, 0, commentLength);
                            if (actual == commentLength)
                            {
                                header_comment[commentLength] = 0;
                                CRC_Calc(header_comment, 0, commentLength);
                                if (sum == crc)
                                {
                                    unpack_size = (archive_header[5] << 24) + (archive_header[4] << 16) + (archive_header[3] << 8) + archive_header[2]; /* unpack size */
                                    pack_size = (archive_header[9] << 24) + (archive_header[8] << 16) + (archive_header[7] << 8) + archive_header[6]; /* packed size */
                                    pack_mode = archive_header[11]; /* pack mode */
                                    crc = (ULONG)((archive_header[25] << 24) + (archive_header[24] << 16) + (archive_header[23] << 8) + archive_header[22]); /* data crc */

                                    fn.Add(new FilenameNode(pack_size, unpack_size, crc, filename, filenameSize));

                                    if (pack_size != 0)
                                    {
                                        switch (pack_mode)
                                        {
                                            case 0: /* store */
                                                abort = ExtractStore(fn);
                                                break;
                                            case 2: /* normal */
                                                abort = ExtractNormal(fn);
                                                break;
                                            default: /* unknown */
                                                break;
                                        }
                                        fn.Clear();
                                    }
                                    else
                                        abort = 0;

                                    if (abort != 0) break; /* a read error occured */
                                }
                            }
                        }
                    }
                }
            } while (abort == 0);
            }
            finally
            {
                br?.Close();
                fs?.Close();
            }
            return abort;
        }
    }
}
