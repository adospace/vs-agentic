using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
            return;

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
            SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    /// <summary>
    /// Assigns a process to the shared job. Call immediately after
    /// <see cref="Process.Start"/>.
    /// </summary>
    public static void AddProcess(Process process)
    {
        if (_jobHandle == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(_jobHandle, process.Handle);
        }
        catch
        {
            // Best effort — the process may have already exited or we may lack
            // permissions (e.g. inside an AppContainer). Don't crash the host.
        }
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
