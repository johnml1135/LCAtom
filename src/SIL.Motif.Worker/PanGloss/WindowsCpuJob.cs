using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SIL.Motif.Worker.PanGloss;

/// <summary>
/// A Windows Job Object that hard-caps every process assigned to it at
/// <see cref="CpuRateHardCapBasisPoints"/> of total machine CPU and terminates every member when the
/// job handle closes.
/// </summary>
/// <remarks>
/// The rate control targets the job's aggregate CPU time rather than any one member, so a job holding a
/// single process is capped exactly as a job holding several (pinned by
/// `ConfiguresCpuRateHardCapAt2500BasisPointsEvenAlone`). <see cref="AssignProcess"/> suspends the
/// process before assigning it, so a child it has not yet spawned cannot start running, and therefore
/// cannot escape the job, before the assignment lands (pinned by
/// `AssignProcess_ContainsTheWholeProcessTreeAcrossItsLifetime`).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuJob : IDisposable
{
    /// <summary>The CPU hard-cap rate, in basis points of one CPU's worth of total machine time.</summary>
    public const int CpuRateHardCapBasisPoints = 2500;

    private readonly SafeJobObjectHandle _handle;
    private bool _disposed;

    /// <summary>Creates a job object and immediately applies the CPU hard cap and kill-on-close limit.</summary>
    public WindowsCpuJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ApplyCpuRateControl();
        ApplyKillOnClose();
    }

    /// <summary>
    /// Suspends <paramref name="process"/>, assigns it (and so its whole future process tree) to this
    /// job, then resumes it.
    /// </summary>
    public void AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ThrowIfDisposed();
        var suspended = NativeMethods.NtSuspendProcess(process.SafeHandle) == 0;
        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (suspended) NativeMethods.NtResumeProcess(process.SafeHandle);
        }
    }

    /// <summary>Terminates every process currently assigned to this job.</summary>
    public void Terminate()
    {
        ThrowIfDisposed();
        if (!NativeMethods.TerminateJobObject(_handle, unchecked((uint)-1)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Reads back the CPU rate control Windows currently has recorded for this job.</summary>
    internal NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION QueryCpuRateControl()
    {
        ThrowIfDisposed();
        return Query<NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(
            NativeMethods.JobObjectInfoClass.JobObjectCpuRateControlInformation);
    }

    /// <summary>Returns how many processes have ever been assigned to this job, including exited ones.</summary>
    internal uint QueryTotalProcessCount()
    {
        ThrowIfDisposed();
        return Query<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(
            NativeMethods.JobObjectInfoClass.JobObjectBasicAccountingInformation).TotalProcesses;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private void ApplyCpuRateControl()
    {
        SetInformation(NativeMethods.JobObjectInfoClass.JobObjectCpuRateControlInformation,
            new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                               NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = CpuRateHardCapBasisPoints,
            });
    }

    private void ApplyKillOnClose()
    {
        SetInformation(NativeMethods.JobObjectInfoClass.JobObjectExtendedLimitInformation,
            new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            });
    }

    private void SetInformation<T>(NativeMethods.JobObjectInfoClass infoClass, T info) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!NativeMethods.SetInformationJobObject(_handle, infoClass, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private T Query<T>(NativeMethods.JobObjectInfoClass infoClass) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(_handle, infoClass, buffer, (uint)size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsCpuJob));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobObjectHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    internal const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    internal enum JobObjectInfoClass
    {
        JobObjectBasicAccountingInformation = 1,
        JobObjectExtendedLimitInformation = 9,
        JobObjectCpuRateControlInformation = 15,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeJobObjectHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(SafeJobObjectHandle hJob, JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(SafeJobObjectHandle hJob, JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AssignProcessToJobObject(SafeJobObjectHandle hJob, SafeHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(SafeJobObjectHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(SafeHandle processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(SafeHandle processHandle);
}
