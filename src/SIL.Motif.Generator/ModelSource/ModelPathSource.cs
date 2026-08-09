namespace SIL.Motif.Generator.ModelSource;

/// <summary>
/// Which of the two candidate locations actually produced the resolved <c>MasterLCModel.xml</c>
/// path. MOT-2 requires this be recorded rather than left implicit (docs/plan-motif.md: "which path
/// is used must be recorded rather than left to whoever runs the build") — it is part of
/// <see cref="Coverage.ModelCoverageReport"/>'s printed output.
/// </summary>
public enum ModelPathSource
{
    /// <summary>The pinned SIL.LCModel NuGet package's <c>contentFiles/</c> — the required path;
    /// this is what makes the generator runnable in CI with no liblcm checkout.</summary>
    NuGetPackageCache,

    /// <summary>A liblcm source checkout. Accepted only as a fallback, never as a requirement
    /// (docs/plan-motif.md MOT-2) — nothing in this repository's CI sets it up.</summary>
    LibLcmCheckout,
}
