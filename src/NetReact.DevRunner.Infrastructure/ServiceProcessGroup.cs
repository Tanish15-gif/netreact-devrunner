using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetReact.DevRunner.Infrastructure;

// The helper waits for the parent before launching the configured command. This lets
// Windows assign it to a job before any descendants exist. Unix exec preserves its PID.
public sealed class ServiceProcessGroup : IDisposable
{
    public const string HelperArgument = "--netreact-service-host";
    private readonly SafeFileHandle? _job;
    private readonly int _id;

    public static void Prepare(ProcessStartInfo start, string serviceHostAssembly)
    {
        var command = start.FileName;
        var arguments = start.ArgumentList.ToArray();
        start.ArgumentList.Clear();
        start.FileName = "dotnet";
        start.ArgumentList.Add(serviceHostAssembly);
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    public ServiceProcessGroup(Process process)
    {
        _id = process.Id;
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObjectW(0, null);
        try
        {
            var limits = new ExtendedLimitInfo { Basic = new BasicLimitInfo { Flags = 0x2000 } };
            if (_job.IsInvalid || !SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf<ExtendedLimitInfo>())
                || !AssignProcessToJobObject(_job, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            _job.Dispose();
            throw;
        }
    }

    public static int RunHelper(string[] args)
    {
        if (args.Length == 0) return 1;
        try
        {
            if (!OperatingSystem.IsWindows() && setsid() < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Console.OpenStandardInput().ReadByte() != 1) return 1;
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo(args[0]) { UseShellExecute = false };
                foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
                using var child = Process.Start(start)!;
                child.WaitForExit();
                return child.ExitCode;
            }

            var strings = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            var argv = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
            try
            {
                Marshal.Copy(strings, 0, argv, strings.Length);
                Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, 0);
                execvp(strings[0], argv);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(argv);
                foreach (var ptr in strings) Marshal.FreeCoTaskMem(ptr);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not launch '{args[0]}': {ex.Message}");
            return 1;
        }
    }

    public bool IsActive()
    {
        if (_job is null) return kill(-_id, 0) == 0;
        if (!QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<Accounting>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return accounting.ActiveProcesses > 0;
    }

    public void RequestStop()
    {
        if (_job is null) kill(-_id, 15);
        else GenerateConsoleCtrlEvent(1, (uint)_id);
    }

    public void ForceStop()
    {
        if (_job is null)
        {
            if (kill(-_id, 9) != 0 && Marshal.GetLastWin32Error() != 3)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else if (!TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInfo
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInfo
    {
        public BasicLimitInfo Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimitInfo limits, int size);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting info, int size, out int returned);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32")]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
    [DllImport("libc", SetLastError = true)]
    private static extern int execvp(nint file, nint argv);
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
