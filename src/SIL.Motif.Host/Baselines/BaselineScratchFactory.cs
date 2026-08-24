using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Runner.DryRun;

namespace SIL.Motif.Host.Baselines;

/// <summary>
/// Opens the <c>.fwdata</c> recorded inside an immutable, already-published Baseline directory
/// directly, for exactly one Dry Run, without copying or extracting the project.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not <see cref="ScratchCacheFactory.CreateFromFileCopy"/>. That factory copies
/// the project directory because its source is a <em>live</em> project a Dry Run must never open in
/// place. A published Baseline directory is different: it is already a discardable, immutable copy —
/// nobody else can be editing it — so a further per-run copy would only pay the copy cost twenty times
/// over for no isolation gained. Opening it directly is the whole performance promise: one saved
/// state, reused by many Dry Runs, with no per-run copy.
/// </para>
/// <para>
/// The published directory must still come back unwritten: this opens the file via
/// <see cref="FwDataProjectLoader.LoadScratchCache"/>, which (per its remarks) keeps the machine-wide
/// writing-system repository out of the picture, and the resulting <see cref="DryRunScratch"/> is
/// mutated in memory and disposed without saving. The caller must never call
/// <c>FwDataProjectLoader.Save</c> on it.
/// </para>
/// </remarks>
public sealed class BaselineScratchFactory
{
    private readonly FwDataProjectLoader _loader;

    public BaselineScratchFactory(FwDataProjectLoader? loader = null)
    {
        _loader = loader ?? new FwDataProjectLoader();
    }

    /// <summary>
    /// Opens <paramref name="publishedFwDataPath"/> — the <c>.fwdata</c> file recorded inside a
    /// published Baseline directory — as a single-use Dry Run scratch, in place.
    /// </summary>
    /// <param name="publishedFwDataPath">
    /// Full path to the <c>.fwdata</c> file inside the published Baseline directory. That directory is
    /// immutable and must survive this call untouched; the returned scratch may be mutated freely and
    /// must be disposed without saving.
    /// </param>
    public DryRunScratch OpenSingleUse(string publishedFwDataPath)
    {
        if (string.IsNullOrWhiteSpace(publishedFwDataPath))
            throw new ArgumentException("Required.", nameof(publishedFwDataPath));
        if (!File.Exists(publishedFwDataPath))
        {
            throw new FileNotFoundException(
                "Published Baseline .fwdata file was not found.", publishedFwDataPath);
        }

        var baselineDirectory = Path.GetDirectoryName(Path.GetFullPath(publishedFwDataPath))
            ?? throw new ArgumentException(
                $"Could not determine the published Baseline directory from '{publishedFwDataPath}'.",
                nameof(publishedFwDataPath));

        var cache = _loader.LoadScratchCache(publishedFwDataPath);
        return DryRunScratch.Adopt(cache, $"published Baseline directory {baselineDirectory}");
    }
}
