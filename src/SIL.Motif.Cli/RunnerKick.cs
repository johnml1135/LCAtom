using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SIL.Motif.Cli;

/// <summary>
/// Starts the job runner after a verb enqueues durable work, so a pseudo-daemon that idled out is woken
/// again rather than left stopped until someone happens to start it by hand.
/// </summary>
/// <remarks>
/// No protocol exists between this and the runner it starts (ADR 0041 decision 5): the two rendezvous
/// through nothing but the named ownership mutex the runner already guards itself with, so spawning is
/// unconditional — an already-alive runner makes this a no-op — and best-effort: a runner that fails to
/// start leaves the row queued for the next enqueue to wake instead of failing the one that just queued it.
/// </remarks>
public static class RunnerKick
{
    /// <summary>Skips the spawn. <b>Test-only</b>: a test that starts and manages a runner itself sets this.</summary>
    public const string SuppressVariable = "MOTIF_SUPPRESS_KICK";

    /// <summary>Overrides where the runner executable is found. <b>Test-only.</b></summary>
    public const string ExecutableVariable = "MOTIF_WORKER_EXE";

    /// <summary>Spawns the runner unless a test has asked to manage one itself.</summary>
    /// <remarks>
    /// Windows <c>CreateProcess</c> duplicates every inheritable handle the caller holds, not just the
    /// ones a child's own <see cref="ProcessStartInfo"/> wires up. Left alone, a caller capturing this
    /// process's own stdio — a redirecting parent, a shell's command substitution — would block on
    /// end-of-file until the spawned runner, which never touches those handles, eventually exits on its
    /// own idle timeout. Marking this process's standard handles non-inheritable first closes that.
    /// </remarks>
    public static void After()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SuppressVariable))) return;
        var executable = Locate();
        if (executable is null) return;
        try
        {
            // Otherwise a caller capturing this process's own stdio would block until the runner exits.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) MakeOwnStandardHandlesNonInheritable();
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // Reported, not thrown: the enqueue already succeeded, and the next enqueue will kick again.
            Console.Error.WriteLine("warning: could not start the background runner (" + exception.Message +
                "). Queued work will run once one is started.");
        }
    }

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    private static void MakeOwnStandardHandlesNonInheritable()
    {
        foreach (var which in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(which);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    private static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "SIL.Motif.Worker.exe"
            : "SIL.Motif.Worker";

        // Published beside the CLI: one artifact, one version (ADR 0040 decision 5).
        var sibling = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(sibling)) return sibling;

        // The unpublished dev tree builds the runner into its own project's own output directory.
        var repoRoot = TryFindRepositoryRoot();
        if (repoRoot is null) return null;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var candidate = Path.Combine(
            repoRoot, "src", "SIL.Motif.Worker", "bin", configuration, "net10.0", fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// Walks up looking for the marker files that identify this repository's root.
    private static string? TryFindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "AGENTS.md")) && Directory.Exists(Path.Combine(dir, "manifest")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
