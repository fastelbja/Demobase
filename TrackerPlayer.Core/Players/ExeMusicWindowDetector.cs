using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TrackerPlayer.Core.Players;

/// <summary>
/// Détecte la fenêtre principale créée par un process exe et ses enfants.
/// Utilisé pour l'intégration PIP des executable music dans DemoBase.
/// </summary>
public static class ExeMusicWindowDetector
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Attend qu'une fenêtre visible appartenant au process (ou à ses enfants)
    /// apparaisse, puis retourne son HWND.
    /// Timeout : 10 secondes. Retourne nint.Zero si aucune fenêtre trouvée.
    /// </summary>
    public static async Task<nint> WaitForWindowAsync(Process process, CancellationToken ct)
    {
        // Collecter les PIDs initiaux (le process peut se terminer vite
        // mais ses enfants continuent — on surveille par nom d'exe aussi)
        var exeName = System.IO.Path.GetFileNameWithoutExtension(process.ProcessName);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            // Chercher une fenêtre visible appartenant au process ou à un
            // process du même nom (cas des launchers qui se re-lancent)
            var hwnd = FindWindowForProcess(process, exeName);
            if (hwnd != nint.Zero)
                return hwnd;

            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return nint.Zero;
    }

    private static nint FindWindowForProcess(Process process, string exeName)
    {
        nint found = nint.Zero;

        // Collecter les PIDs candidats
        var candidatePids = new System.Collections.Generic.HashSet<uint>();
        try
        {
            if (!process.HasExited)
                candidatePids.Add((uint)process.Id);
        }
        catch { }

        // Ajouter les process du même nom (sub-process launchers)
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
                candidatePids.Add((uint)p.Id);
        }
        catch { }

        if (candidatePids.Count == 0) return nint.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetParent(hWnd) != nint.Zero) return true; // fenêtre enfant, pas top-level
            if (GetWindowTextLength(hWnd) == 0) return true; // pas de titre = pas une vraie fenêtre

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!candidatePids.Contains(pid)) return true;

            // Vérifier que la fenêtre a une taille raisonnable (> 100x100)
            if (GetWindowRect(hWnd, out var rect))
            {
                var w = rect.Right - rect.Left;
                var h = rect.Bottom - rect.Top;
                if (w < 100 || h < 100) return true;
            }

            found = hWnd;
            return false; // stop enumeration
        }, nint.Zero);

        return found;
    }
}
