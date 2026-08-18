using System;
using System.Runtime.InteropServices;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Binding P/Invoke vers SndhPlayer.dll, compilée à partir de la lib AtariAudio
    /// du projet sndh-player d'Arnaud Carré (github.com/arnaud-carre/sndh-player).
    ///
    /// Émulation complète Atari ST (68000 via Musashi + YM2149 + STE DAC) pour
    /// lire correctement les fichiers SNDH, que ZXTune/UADE ne supportent pas.
    /// La décompression ICE! est gérée automatiquement en interne par la DLL.
    /// </summary>
    internal static class SndhNative
    {
        private const string DllName = "SndhPlayer.dll";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct SndhSubSongInfoNative
        {
            public int subsongCount;
            public int playerTickCount;
            public int playerTickRate;
            public int samplePerTick;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string musicName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string musicAuthor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string year;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Sndh_Create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Sndh_Destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_Load(IntPtr handle, byte[] rawData, int rawSize, uint hostReplayRate);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Sndh_Unload(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_IsLoaded(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_GetSubsongCount(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_GetDefaultSubsong(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_GetSubsongInfo(IntPtr handle, int subSongId, out SndhSubSongInfoNative outInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_InitSubSong(IntPtr handle, int subSongId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Sndh_AudioRender(IntPtr handle, short[] buffer, int count, IntPtr pSampleViewInfo);
    }
}
