using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cop.Core;

/// <summary>Windows job ownership keeps descendants killable even after the launcher exits.</summary>
internal sealed class ToolchainProcessContainment : IDisposable
{
    private readonly JobHandle? _job;

    public ToolchainProcessContainment()
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error, "Cannot configure toolchain process ownership.");
        }
    }

    public void Attach(Process process)
    {
        if (_job is not null && !AssignProcessToJobObject(_job, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain toolchain process.");
    }

    public void Terminate()
    {
        if (_job is not null && !TerminateJobObject(_job, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate toolchain process descendants.");
        if (_job is not null)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<AccountingInformation>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect toolchain process cleanup.");
                if (accounting.ActiveProcesses == 0) break;
                if (timer.Elapsed > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("Toolchain descendants did not exit after job termination.");
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose()
    {
        try { Terminate(); }
        finally { _job?.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out AccountingInformation information, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
