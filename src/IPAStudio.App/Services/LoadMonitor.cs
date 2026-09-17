using System.Diagnostics;

namespace IPAStudio.App.Services;

/// <summary>
/// Measures what this app is costing the machine right now: CPU, memory, and how many helper
/// processes it has running.
///
/// Written because "it sometimes loads the processor" is impossible to act on without a number.
/// Task Manager attributes the work to whichever helper is spending it — ipatool, idevice*,
/// a dozen at a time — so the app looks idle while its children are busy. This adds the
/// children's CPU to our own, which is the figure the user actually feels.
///
/// CPU is deliberately sampled rather than read: Windows exposes cumulative processor time, so
/// a percentage only exists as a difference between two readings. The first call after a reset
/// therefore has nothing to compare against and reports zero rather than a made-up number.
/// </summary>
public sealed class LoadMonitor
{
    /// <summary>
    /// Helper executables this app spawns, matched by name because they are launched as plain
    /// child processes with no job object tying them to us.
    ///
    /// Kept to the binaries the app actually starts. Apple's own Mobile Device Service is
    /// excluded on purpose: it is shared with iTunes and outlives us, so charging its work to
    /// this app would blame us for load we did not cause. Device log capture is absent for the
    /// opposite reason — it runs through the native library in-process, so it is already
    /// counted as our own CPU.
    /// </summary>
    private static readonly string[] HelperNames =
    {
        "ipatool", "anisette", "idevice_id", "ideviceinfo", "ideviceinstaller",
        "idevicepair", "idevicediagnostics",
    };

    private readonly Process _self = Process.GetCurrentProcess();

    private TimeSpan _lastCpuTotal;
    private DateTimeOffset _lastSampleAt;
    private bool _primed;

    /// <summary>The number of logical processors, so CPU time can be scaled to a percentage.</summary>
    private static readonly int ProcessorCount = Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Takes a reading. Returns null on the very first call, where there is no previous sample
    /// to measure against.
    /// </summary>
    public LoadSample? Sample()
    {
        var now = DateTimeOffset.UtcNow;

        var helpers = FindHelpers();

        TimeSpan cpuTotal;
        long memoryBytes;
        int helperCount;
        try
        {
            cpuTotal = TotalCpuTime(helpers);
            memoryBytes = TotalMemory(helpers);
            helperCount = helpers.Count;
        }
        finally
        {
            // Each Process holds an OS handle. This samples once a second for as long as the
            // page is open, so leaking them would turn a diagnostic into its own slow leak.
            foreach (var helper in helpers)
                helper.Dispose();
        }

        var elapsed = now - _lastSampleAt;
        var cpuDelta = cpuTotal - _lastCpuTotal;

        _lastCpuTotal = cpuTotal;
        _lastSampleAt = now;

        if (!_primed)
        {
            _primed = true;
            return null;
        }

        // A vanishingly small window would divide by almost nothing and produce a nonsense
        // spike, so it is treated as no reading at all.
        if (elapsed <= TimeSpan.FromMilliseconds(50)) return null;

        // Clamped at the top: a process tree can legitimately exceed one core, but reporting
        // "340%" to a user asking whether the app is heavy is worse than reporting "100%".
        var percent = cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * ProcessorCount) * 100.0;
        percent = Math.Clamp(percent, 0, 100);

        return new LoadSample(percent, memoryBytes, helperCount);
    }

    /// <summary>Forgets the previous reading, so the next one starts a fresh window.</summary>
    public void Reset()
    {
        _primed = false;
        _lastCpuTotal = TimeSpan.Zero;
        _lastSampleAt = DateTimeOffset.UtcNow;
    }

    private List<Process> FindHelpers()
    {
        var found = new List<Process>();

        foreach (var name in HelperNames)
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(name);
            }
            catch
            {
                // Enumeration can fail transiently; a missing helper is not worth surfacing.
                continue;
            }

            found.AddRange(matches);
        }

        return found;
    }

    private TimeSpan TotalCpuTime(List<Process> helpers)
    {
        var total = SafeCpu(_self);

        foreach (var helper in helpers)
            total += SafeCpu(helper);

        return total;
    }

    private long TotalMemory(List<Process> helpers)
    {
        var total = SafeMemory(_self);

        foreach (var helper in helpers)
            total += SafeMemory(helper);

        return total;
    }

    /// <summary>
    /// Reads a process's CPU time, treating any failure as zero. Helpers are short-lived by
    /// design — one may well exit between being listed and being read — and an exception
    /// there must not take down the whole reading.
    /// </summary>
    private static TimeSpan SafeCpu(Process process)
    {
        try
        {
            process.Refresh();
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static long SafeMemory(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>One reading of what the app is currently using.</summary>
/// <param name="CpuPercent">Share of total processor capacity, app plus helpers.</param>
/// <param name="MemoryBytes">Working set, app plus helpers.</param>
/// <param name="HelperCount">Helper processes running right now.</param>
public sealed record LoadSample(double CpuPercent, long MemoryBytes, int HelperCount);
