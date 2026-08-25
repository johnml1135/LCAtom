using System.Diagnostics;
using System.Runtime.Versioning;
using SIL.Motif.Worker.PanGloss;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>Skips at discovery on a non-Windows host rather than fail on APIs that do not exist there.</summary>
public sealed class RequiresWindowsFactAttribute : FactAttribute
{
    public RequiresWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows Job Objects are only available on Windows.";
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCpuJobTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    // Proves the CONFIGURED limit only; timing-dependent OS-level enforcement is not measured here.
    [RequiresWindowsFact]
    public void ConfiguresCpuRateHardCapAt2500BasisPointsEvenAlone()
    {
        using var job = new WindowsCpuJob();

        var control = job.QueryCpuRateControl();

        Assert.Equal((uint)WindowsCpuJob.CpuRateHardCapBasisPoints, control.CpuRate);
        Assert.Equal(NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            control.ControlFlags);
    }

    [RequiresWindowsFact]
    public void AssignProcess_ContainsTheWholeProcessTreeAcrossItsLifetime()
    {
        using var process = StartBoundedProcess("ping -n 2 127.0.0.1 >nul");
        using var job = new WindowsCpuJob();
        try
        {
            job.AssignProcess(process);

            var exited = process.WaitForExit(BoundedWait);
            Assert.True(exited, "The bounded ping helper process did not exit in time.");
            // Counts cmd.exe plus the ping.exe it spawns: job membership reached a not-yet-created child.
            Assert.True(job.QueryTotalProcessCount() >= 2);
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    [RequiresWindowsFact]
    public void Terminate_EndsTheAssignedProcessPromptly()
    {
        using var process = StartBoundedProcess("ping -n 30 127.0.0.1 >nul");
        using var job = new WindowsCpuJob();
        try
        {
            job.AssignProcess(process);

            job.Terminate();

            Assert.True(process.WaitForExit(BoundedWait), "Terminate did not end the assigned process.");
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    [RequiresWindowsFact]
    public void Dispose_KillsTheAssignedProcessViaKillOnClose()
    {
        using var process = StartBoundedProcess("ping -n 30 127.0.0.1 >nul");
        var job = new WindowsCpuJob();
        try
        {
            job.AssignProcess(process);

            job.Dispose();

            Assert.True(process.WaitForExit(BoundedWait), "Closing the job did not kill its member process.");
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    // Bounded cmd.exe/ping.exe helper; always killed below regardless of outcome.
    private static Process StartBoundedProcess(string arguments)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cmd.exe.");
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited between the check and the kill */ }
    }
}
