using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace costats.Infrastructure.Antigravity;

/// <summary>
/// A process started inside its own Windows job object, hidden, with no
/// redirected standard streams and an explicit environment block. The process
/// is in the job from the moment it is created (through
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>), so it and everything it starts can be
/// stopped together. The job is created with only
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the last job handle, even
/// when this app crashes, ends every process in it.
/// </summary>
internal sealed class JobObjectProcess : IDisposable
{
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private static readonly IntPtr ProcThreadAttributeJobList = (IntPtr)0x0002000D;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint WaitObject0 = 0;

    private readonly SafeJobHandle _job;
    private readonly SafeProcessHandle _process;
    private int _disposed;

    private JobObjectProcess(SafeJobHandle job, SafeProcessHandle process, int processId)
    {
        _job = job;
        _process = process;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    /// <summary>True once the started process itself has exited (its children may still run).</summary>
    public bool HasExited => WaitForSingleObject(_process, 0) == WaitObject0;

    /// <summary>
    /// Starts <paramref name="executable"/> with the full command line
    /// <paramref name="commandLine"/> (which must begin with the quoted
    /// executable) in <paramref name="workingDirectory"/>. Fails closed: when
    /// any step fails, the job is closed, which ends anything already in it,
    /// and a <see cref="Win32Exception"/> is thrown.
    /// </summary>
    public static JobObjectProcess Start(
        string executable,
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentException.ThrowIfNullOrEmpty(commandLine);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw error;
        }

        var attributeList = IntPtr.Zero;
        var jobArray = IntPtr.Zero;
        var commandBuffer = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var attributeListInitialized = false;
        var jobAddRef = false;
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributeListInitialized = true;
            job.DangerousAddRef(ref jobAddRef);
            jobArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobArray, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeJobList, jobArray, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.lpAttributeList = attributeList;

            // CreateProcessW may write to the command line buffer, so it gets its own copy.
            commandBuffer = Marshal.StringToHGlobalUni(commandLine);
            environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));

            if (!CreateProcess(
                    executable,
                    commandBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: false,
                    CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(processInformation.hThread);
            var process = new SafeProcessHandle(processInformation.hProcess, ownsHandle: true);
            return new JobObjectProcess(job, process, processInformation.dwProcessId);
        }
        catch
        {
            // Closing the only job handle ends anything that got into the job.
            if (jobAddRef)
            {
                job.DangerousRelease();
                jobAddRef = false;
            }

            job.Dispose();
            throw;
        }
        finally
        {
            if (jobAddRef)
            {
                job.DangerousRelease();
            }

            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            if (jobArray != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(jobArray);
            }

            if (commandBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(commandBuffer);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    /// <summary>
    /// Builds a Windows command line for <paramref name="executable"/> and
    /// <paramref name="arguments"/> with the standard argv quoting rules, so
    /// each argument arrives unchanged.
    /// </summary>
    public static string BuildCommandLine(string executable, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendQuotedArgument(builder, argument);
        }

        return builder.ToString();
    }

    internal static void AppendQuotedArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>name=value pairs sorted by name, each ending in a null, the block ending in a second null.</summary>
    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment
                     .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Key.IndexOf('=', 1) < 0 && pair.Key.IndexOf('\0') < 0)
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append((pair.Value ?? string.Empty).Replace("\0", string.Empty)).Append('\0');
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        builder.Append('\0');
        return builder.ToString();
    }

    /// <summary>Whether the started process is in this object's job.</summary>
    public bool IsInJob()
    {
        return IsProcessInJob(_process, _job, out var result) && result;
    }

    /// <summary>The job's limit flags, read back from Windows.</summary>
    public uint QueryLimitFlags()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        if (!QueryInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return info.BasicLimitInformation.LimitFlags;
    }

    /// <summary>How many processes are running in the job right now.</summary>
    public int ActiveProcessCount()
    {
        var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        if (!QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, ref info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (int)info.ActiveProcesses;
    }

    /// <summary>The ids of the processes running in the job right now.</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        const int capacity = 64;
        const int headerSize = 8; // two DWORD counts, then the ULONG_PTR id array
        var size = headerSize + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new List<int>(count);
            for (var index = 0; index < Math.Min(count, capacity); index++)
            {
                ids.Add((int)Marshal.ReadIntPtr(buffer, headerSize + index * IntPtr.Size));
            }

            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Ends every process in the job and waits briefly for the started one to go.</summary>
    public void Terminate(TimeSpan wait)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        TerminateJobObject(_job, 1);
        WaitForSingleObject(_process, (uint)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _process.Dispose();
        _job.Dispose();
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(
        string lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob,
        int jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle hJob,
        int jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle hJob,
        int jobObjectInformationClass,
        ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        int cbJobObjectInformationLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle processHandle, SafeJobHandle jobHandle, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
