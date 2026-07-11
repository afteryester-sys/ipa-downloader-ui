using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IPAStudio.Core.Tools;

/// <summary>
/// Wraps a Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Every child process (ipatool, anisette, libimobiledevice, installers, ...) is assigned
/// to this job. When the parent process exits for ANY reason — normal close, crash, or
/// being killed from Task Manager — Windows terminates every process in the job. This
/// guarantees no orphaned tools keep the portable folder locked.
///
/// A concurrent list of assigned processes is also kept as a managed fallback so we can
/// kill them explicitly during a graceful shutdown.
/// </summary>
public sealed class ProcessJobObject : IDisposable
{
    private readonly ConcurrentBag<Process> _tracked = new();
    private readonly IntPtr _handle;
    private readonly bool _enabled;
    private bool _disposed;

    public ProcessJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            _enabled = false;
            return;
        }

        try
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                _enabled = false;
                return;
            }

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr extendedPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extended, extendedPtr, false);
                _enabled = SetInformationJobObject(
                    _handle,
                    JobObjectExtendedLimitInformation,
                    extendedPtr,
                    (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(extendedPtr);
            }
        }
        catch
        {
            _enabled = false;
        }
    }

    /// <summary>Assigns a freshly started process to the job (and tracks it for fallback kills).</summary>
    public void Track(Process process)
    {
        _tracked.Add(process);

        if (!_enabled || _handle == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(_handle, process.Handle);
        }
        catch
        {
            // Process may have already exited, or assignment is unsupported; the
            // managed fallback in KillAll() still covers this process.
        }
    }

    /// <summary>Force-terminates every tracked process tree. Safe to call multiple times.</summary>
    public void KillAll()
    {
        foreach (var process in _tracked)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited / no access.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Managed best-effort kill first...
        KillAll();

        // ...then close the job handle, which triggers KILL_ON_JOB_CLOSE for anything left.
        if (_enabled && _handle != IntPtr.Zero)
        {
            try { CloseHandle(_handle); } catch { }
        }
    }

    // ---- P/Invoke ----

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
}
