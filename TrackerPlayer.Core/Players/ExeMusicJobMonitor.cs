using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TrackerPlayer.Core.Players;

/// <summary>
/// Surveille un arbre de process complet via Windows Job Objects.
/// 
/// Problème résolu : les executable music (.exe) lancent souvent un sous-process
/// (self-extractor, launcher, unpacker) et se terminent immédiatement. Sans cette
/// surveillance, WaitForExitAsync() retourne trop tôt et DemoBase passe au suivant
/// alors que la musique joue encore dans le sous-process.
/// 
/// Solution : on crée un Job Object, on y attache le process principal, et on
/// attend via un CompletionPort que TOUS les process du Job soient terminés —
/// y compris les sous-process hérités automatiquement.
///
/// 2026-08-02, retour utilisateur ("je croyais que le process de la musique
/// executable était lié à demobase, de sorte que le process soit tué si je
/// quittais demobase. je viens de quitter demobase et la musique exe continue à
/// tourner") : ce Job Object n'était jusqu'ici utilisé QUE pour la SURVEILLANCE
/// (CompletionPort ci-dessous, pour détecter la fin de l'arbre de process et
/// avancer la playlist) — jamais pour en forcer l'arrêt. Sans le flag
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (posé dans WaitForProcessTreeAsync
/// ci-dessous), fermer/perdre le handle du Job ne tue PAS ses process : Windows
/// les détache simplement, ils continuent de tourner en autonome. Or c'est
/// exactement ce qui pouvait se produire : DemoBase.OnExit tue son propre process
/// (Process.GetCurrentProcess().Kill(), cf. App.xaml.cs — volontaire, pour éviter
/// les finalizers CLR bloquants) après avoir lancé KillAll() en arrière-plan sur
/// un thread du ThreadPool (Task.Run) ; ce thread est un thread d'ARRIÈRE-PLAN
/// (non-foreground), et .NET ne garantit PAS qu'il ait le temps de s'exécuter
/// avant que le process ne se termine — la fenêtre "finally" ci-dessous
/// (TerminateJobObject/CloseHandle) peut donc ne jamais s'exécuter, et l'ancien
/// KillAll() côté App.xaml.cs peut lui aussi perdre la course. Avec
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, c'est WINDOWS LUI-MÊME qui tue tout le Job
/// dès que son dernier handle se ferme — y compris si DemoBase.exe se termine
/// brutalement (crash, Gestionnaire des tâches, coupure), sans dépendre d'une
/// seule ligne de notre code managé pour s'exécuter à temps.
/// </summary>
internal static class ExeMusicJobMonitor
{
    // ── Win32 API ────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int jobObjectInfoClass,
        ref JOBOBJECT_ASSOCIATE_COMPLETION_PORT info, int cbJobObjectInfoLength);

    // 2026-08-02 : second overload (même export natif, struct différente) pour
    // poser JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE — cf. commentaire de classe.
    // Plusieurs déclarations [DllImport] vers le même export Win32, différenciées
    // par le type du paramètre marshalé, est un pattern P/Invoke standard.
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetInformationJobObject")]
    private static extern bool SetInformationJobObjectExtendedLimit(nint hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateIoCompletionPort(nint fileHandle, nint existingPort,
        nuint completionKey, uint numberOfConcurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(nint completionPort,
        out uint lpNumberOfBytes, out nuint lpCompletionKey,
        out nint lpOverlapped, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(nint hJob, uint uExitCode);

    private const int JobObjectAssociateCompletionPortInformation = 7;
    // 2026-08-02 : classe d'info pour poser des limites (dont KILL_ON_JOB_CLOSE),
    // distincte de JobObjectAssociateCompletionPortInformation ci-dessus — les deux
    // classes d'info coexistent sans conflit sur le même Job Object.
    private const int JobObjectExtendedLimitInformation           = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE          = 0x00002000;
    private const uint JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO        = 4;
    private const nint INVALID_HANDLE_VALUE                       = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT
    {
        public nint CompletionKey;
        public nint CompletionPort;
    }

    // 2026-08-02 : structures Win32 nécessaires pour JobObjectExtendedLimitInformation
    // (JOBOBJECT_EXTENDED_LIMIT_INFORMATION) — seul BasicLimitInformation.LimitFlags
    // nous intéresse ici, mais la structure complète doit être présente et de la
    // bonne taille (Marshal.SizeOf) pour que l'appel Win32 soit correctement formé.
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long   PerProcessUserTimeLimit;
        public long   PerJobUserTimeLimit;
        public uint   LimitFlags;
        public nuint  MinimumWorkingSetSize;
        public nuint  MaximumWorkingSetSize;
        public uint   ActiveProcessLimit;
        public nuint  Affinity;
        public uint   PriorityClass;
        public uint   SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS                       IoInfo;
        public nuint                              ProcessMemoryLimit;
        public nuint                              JobMemoryLimit;
        public nuint                              PeakProcessMemoryUsed;
        public nuint                              PeakJobMemoryUsed;
    }

    // ── API publique ─────────────────────────────────────────────────────────

    /// <summary>
    /// Attache <paramref name="process"/> à un Job Object et attend que
    /// TOUS les process de l'arbre (y compris les sous-process) soient terminés.
    /// Respecte <paramref name="ct"/> pour un arrêt propre via Stop().
    /// </summary>
    public static async Task WaitForProcessTreeAsync(Process process, CancellationToken ct)
    {
        nint hJob  = nint.Zero;
        nint hPort = nint.Zero;
        try
        {
            hPort = CreateIoCompletionPort(INVALID_HANDLE_VALUE, nint.Zero, nuint.Zero, 1);
            if (hPort == nint.Zero) { await FallbackWaitAsync(process, ct); return; }

            hJob = CreateJobObject(nint.Zero, null);
            if (hJob == nint.Zero) { await FallbackWaitAsync(process, ct); return; }

            // 2026-08-02, retour utilisateur ("la musique exe continue à tourner
            // après avoir quitté demobase") : demander à Windows de tuer TOUT le
            // Job dès que son dernier handle se ferme (donc y compris si
            // DemoBase.exe se termine brutalement, sans exécuter le moindre code
            // managé de nettoyage) — cf. commentaire de classe ci-dessus pour le
            // détail de la course perdue par l'ancien mécanisme (KillAll() en
            // arrière-plan côté App.xaml.cs).
            var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            SetInformationJobObjectExtendedLimit(hJob, JobObjectExtendedLimitInformation,
                ref limitInfo, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

            var assoc = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
            {
                CompletionKey  = hJob,
                CompletionPort = hPort,
            };
            SetInformationJobObject(hJob, JobObjectAssociateCompletionPortInformation,
                ref assoc, Marshal.SizeOf<JOBOBJECT_ASSOCIATE_COMPLETION_PORT>());

            // Attacher le process au Job avant qu'il puisse créer des sous-process
            if (!AssignProcessToJobObject(hJob, process.Handle))
            {
                // Déjà dans un job (ex: lancé depuis VS en mode debug) → fallback
                await FallbackWaitAsync(process, ct);
                return;
            }

            // Attendre sur le CompletionPort dans un thread pool (non-bloquant)
            await Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    // Poll toutes les 200ms pour respecter l'annulation
                    bool ok = GetQueuedCompletionStatus(hPort,
                        out uint bytes, out nuint key, out _, 200);

                    if (ct.IsCancellationRequested) break;

                    if (ok && bytes == JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO
                           && key == (nuint)hJob)
                    {
                        // Plus aucun process actif dans le Job → fin de l'arbre
                        break;
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Stop() appelé */ }
        catch
        {
            // En cas d'erreur API, fallback sur WaitForExit simple
            try { await FallbackWaitAsync(process, ct); } catch { }
        }
        finally
        {
            if (hJob  != nint.Zero) { TerminateJobObject(hJob, 0); CloseHandle(hJob); }
            if (hPort != nint.Zero) CloseHandle(hPort);
        }
    }

    /// <summary>
    /// Fallback si le Job Object ne peut pas être créé (ex: process déjà dans un job,
    /// ou droits insuffisants). Attend simplement la fin du process principal + 2s.
    /// </summary>
    private static async Task FallbackWaitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
            // Attendre 2s pour laisser les sous-process éventuels s'initialiser
            if (!ct.IsCancellationRequested)
                await Task.Delay(500, ct);
        }
        catch (OperationCanceledException)
        {
            // 2026-08-02 : ce chemin fallback n'utilise PAS de Job Object (donc pas
            // de TerminateJobObject dans le finally de WaitForProcessTreeAsync, qui
            // ne s'exécute que si hJob a été créé) — sans ce Kill() ici, un Stop()
            // (annulation) ne tuerait plus JAMAIS le process dans ce cas précis
            // (Job Object indisponible : déjà dans un job non-imbriqué, droits
            // insuffisants…). Contrairement à ExeMusicPlayer.Stop() (qui ne tue
            // plus directement, cf. son commentaire — pour éviter la course avec
            // TerminateJobObject), il n'y a ICI qu'UN SEUL mécanisme de
            // terminaison possible (pas de Job Object en jeu), donc aucun risque
            // de double-kill concurrent.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
