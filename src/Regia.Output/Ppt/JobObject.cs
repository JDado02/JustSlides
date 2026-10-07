using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Regia.Output.Ppt;

/// <summary>
/// Job Object con JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: se la regia muore (anche di colpo) Windows chiude l'handle
/// e termina tutti i processi assegnati (PptHost e il POWERPNT.EXE avviato da noi). PowerPoint, lanciato via COM,
/// NON è figlio di PptHost (lo avvia il servizio DCOM): va assegnato al job esplicitamente.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            Log.Warning("Job Object non creato (errore {Error})", Marshal.GetLastWin32Error());
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Log.Warning("Job Object: impossibile impostare KILL_ON_JOB_CLOSE (errore {Error})", Marshal.GetLastWin32Error());
            Dispose();
        }
    }

    public bool IsValid => _handle != IntPtr.Zero;

    /// <summary>Assegna un processo al job. Falso (e log) se non riesce: la regia continua con i soli kill del watchdog.</summary>
    public bool Assign(Process process)
    {
        if (_handle == IntPtr.Zero)
            return false;

        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
                return true;

            Log.Warning("Job Object: processo {Pid} non assegnato (errore {Error})", process.Id, Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Job Object: processo non assegnato");
        }

        return false;
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
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
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
