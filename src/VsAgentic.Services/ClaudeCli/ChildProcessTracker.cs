using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Uses a Win32 Job Object to ensure child processes are killed when the
/// parent process exits — including on crash or forced termination.
///
/// How it works:
///  1. On first use, we create a Job with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
///  2. Each child process is assigned to the Job via <see cref="AddProcess"/>.
///  3. The Job handle is held open for the lifetime of this process. When the
///     parent exits (cleanly or via crash), the OS closes the handle, which
///     terminates all assigned child processes.
///
/// This is a singleton — one job per extension host process.
/// </summary>
internal static class ChildProcessTracker
{
    private static readonly IntPtr _jobHandle;

    static ChildProcessTracker()
    {
        _jobHandle = CreateKillOnCloseJob();
    }

    /// <summary>
    /// Creates a job that ends every process in it when its last handle
    /// closes. Returns <see cref="IntPtr.Zero"/> if the job cannot be created.
    /// </summary>
    internal static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length))
            {
                // A job without the limit would not end anything; do not hand it out.
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
        return job;
    }

    /// <summary>
    /// Assigns a process to the shared job. Call immediately after
    /// <see cref="Process.Start"/>.
    /// </summary>
    public static void AddProcess(Process process) => TryAssign(_jobHandle, process);

    internal static bool TryAssign(IntPtr job, Process process)
    {
        if (job == IntPtr.Zero) return false;
        try
        {
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // Best effort — the process may have already exited or we may lack
            // permissions (e.g. inside an AppContainer). Don't crash the host.
            return false;
        }
    }

    internal static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    // --- P/Invoke ---

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [Flags]
    private enum JOBOBJECTLIMIT : uint
    {
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JOBOBJECTLIMIT LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// A job for one CLI run. Everything the CLI starts — the MCP helper, shells,
/// background commands — joins it, so <see cref="Dispose"/> ends the whole
/// tree. <see cref="Process.Kill()"/> on net472 ends only the CLI itself.
///
/// The CLI also stays in the shared job of <see cref="ChildProcessTracker"/>;
/// this one nests inside it (Windows 8 and later).
/// </summary>
internal sealed class ProcessTreeJob : IDisposable
{
    private IntPtr _handle;

    private ProcessTreeJob(IntPtr handle) => _handle = handle;

    /// <summary>Returns null if the job cannot be created or the process not assigned.</summary>
    public static ProcessTreeJob? TryCreate(Process process)
    {
        var handle = ChildProcessTracker.CreateKillOnCloseJob();
        if (handle == IntPtr.Zero) return null;
        if (ChildProcessTracker.TryAssign(handle, process)) return new ProcessTreeJob(handle);
        ChildProcessTracker.Close(handle);
        return null;
    }

    /// <summary>Ends every process still in the job.</summary>
    public void Dispose()
    {
        ChildProcessTracker.Close(Interlocked.Exchange(ref _handle, IntPtr.Zero));
    }
}
