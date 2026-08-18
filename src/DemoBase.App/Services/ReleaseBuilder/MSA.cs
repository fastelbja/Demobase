using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services
{
    public class MSA
    {
        private static readonly int BYTES_PER_SECTOR = 512;

        private struct MsaImageInfo
        {
            public int SectorSize;
            public int StartTrack;
            public int EndTrack;
            public int SectorsPerTrack;
            public int NumHeads;
            public int NumTracks;
            public int TotalSectors;
            public byte[] MSAData;
            public byte[] diskData;
        }

        //#define READ_M8(address, offset) (address[offset])
        private static int READ_M16(byte[] data, int offset)
        {
            return ((data[offset + 1] & 0xff) | ((data[offset] & 0xff) << 8));
        }

        //#define READ_M32(address, offset) ((address[offset + 3] & 0xff) | ((address[offset + 2] & 0xff) << 8) | ((address[offset + 1] & 0xff) << 16) | ((address[offset] & 0xff) << 24))

        public static int DecodeMSA(string filePath,string fileDest)
        {
            FileStream fsMSA = new(filePath,FileMode.Open,FileAccess.Read);
            BinaryReader br = new(fsMSA);

            MsaImageInfo info = new MsaImageInfo();

            info.MSAData = new byte[br.BaseStream.Length];
            br.Read(info.MSAData,0,info.MSAData.Length);
            br.Close();
            fsMSA.Close();

            info.SectorSize = BYTES_PER_SECTOR;
            info.SectorsPerTrack = READ_M16(info.MSAData, 2);
            info.NumHeads = READ_M16(info.MSAData, 4) + 1;
            info.StartTrack = READ_M16(info.MSAData, 6);
            info.EndTrack = READ_M16(info.MSAData, 8);
            info.NumTracks = info.EndTrack + 1;
            info.TotalSectors = info.NumTracks * info.SectorsPerTrack * info.NumHeads;

            // Protection overflow : valeurs MSA invalides → abandon
            const int maxDiskSize = 10 * 1024 * 1024; // 10 Mo max (disquette = ~1 Mo)
            long diskSize = (long)info.TotalSectors * info.SectorSize;
            if (diskSize <= 0 || diskSize > maxDiskSize) return -1;

            info.diskData = new byte[diskSize];

            int errorCode = DecodeMsaImageToDiskImage(info);

            if(errorCode==0)
            {
                FileStream fsST = new(fileDest,FileMode.CreateNew,FileAccess.Write);
                BinaryWriter bw = new(fsST);
                bw.Write(info.diskData,0,info.diskData.Length);
                bw.Close();
                fsST.Close();
            }

            return errorCode;
        }
        private static int DecodeMsaImageToDiskImage(MsaImageInfo pMsaImageInfo)
        {
            int pMsaPointer = 10;
            int pDiskPointer = pMsaImageInfo.StartTrack * pMsaImageInfo.SectorsPerTrack * pMsaImageInfo.NumHeads * pMsaImageInfo.SectorSize;
            int pEndPointer;
            int iTrackIndex;
            int iHeadIndex;
            int iNumBytes;
            byte cMsaData;
            byte cRleData;
            int iRleCount;

            try
            {
                for (iTrackIndex = pMsaImageInfo.StartTrack; iTrackIndex <= pMsaImageInfo.EndTrack; iTrackIndex++)
                {
                    for (iHeadIndex = 0; iHeadIndex < pMsaImageInfo.NumHeads; iHeadIndex++)
                    {
                        iNumBytes = (pMsaImageInfo.MSAData[pMsaPointer++] & 0xff) << 8;
                        iNumBytes |= pMsaImageInfo.MSAData[pMsaPointer++] & 0xff;

                        if (iNumBytes < pMsaImageInfo.SectorsPerTrack * pMsaImageInfo.SectorSize)
                        {
                            pEndPointer = pMsaPointer + iNumBytes;

                            while (pMsaPointer < pEndPointer)
                            {
                                cMsaData = pMsaImageInfo.MSAData[pMsaPointer++];

                                if (cMsaData != 0xe5)
                                {
                                    pMsaImageInfo.diskData[pDiskPointer++] = cMsaData;
                                }
                                else
                                {
                                    cRleData = pMsaImageInfo.MSAData[pMsaPointer++];

                                    iRleCount = (pMsaImageInfo.MSAData[pMsaPointer++] & 0xff) << 8;
                                    iRleCount |= pMsaImageInfo.MSAData[pMsaPointer++] & 0xff;

                                    while (iRleCount != 0)
                                    {
                                        pMsaImageInfo.diskData[pDiskPointer++] = cRleData;
                                        iRleCount--;
                                    }
                                }
                            }
                        }
                        else
                        {
                            while (iNumBytes > 0)
                            {
                                pMsaImageInfo.diskData[pDiskPointer++] = pMsaImageInfo.MSAData[pMsaPointer++];
                                iNumBytes--;
                            }
                        }
                    }
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }
    }
}
