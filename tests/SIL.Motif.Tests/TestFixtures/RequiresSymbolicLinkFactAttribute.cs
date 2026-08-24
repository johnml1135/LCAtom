using Xunit;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Probes, once per run, whether this process can create filesystem symbolic links.
/// </summary>
/// <remarks>
/// On Windows, <see cref="System.IO.Directory.CreateSymbolicLink(string, string)"/> and its
/// <see cref="System.IO.File"/> counterpart throw "a required privilege is not held by the client"
/// unless the process is elevated (or Developer Mode grants <c>SeCreateSymbolicLinkPrivilege</c>).
/// Tests that exercise reparse-point handling must know this up front rather than discover it by
/// catching the failure mid-test, which would let the test pass having asserted nothing.
/// </remarks>
public static class SymbolicLinkCapability
{
    private static readonly Lazy<bool> Probe = new(ProbeOnce);

    public static bool IsSupported => Probe.Value;

    private static bool ProbeOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "SIL.Motif.SymbolicLinkProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort: probe cleanup only */ }
        }
    }
}

/// <summary>
/// Skips at discovery when this process cannot create filesystem symbolic links, rather than let a
/// test's own try/catch around <c>CreateSymbolicLink</c> swallow the privilege failure and pass having
/// asserted nothing. On an elevated machine (or with Developer Mode's symlink privilege), it runs.
/// </summary>
public sealed class RequiresSymbolicLinkFactAttribute : FactAttribute
{
    public RequiresSymbolicLinkFactAttribute()
    {
        if (!SymbolicLinkCapability.IsSupported)
            Skip = "Creating filesystem symbolic links requires Administrator elevation (or Developer " +
                   "Mode's SeCreateSymbolicLinkPrivilege) on this machine.";
    }
}
