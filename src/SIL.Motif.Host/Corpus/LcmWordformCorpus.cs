using SIL.LCModel;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Extracts a <see cref="Selection"/> from a live <see cref="LcmCache"/>'s wordforms.
/// </summary>
/// <remarks>
/// <para>
/// <b>The surface form is <c>Form</c> in the project's default vernacular writing system</b> —
/// <c>IWfiWordform.Form</c> is a multi-string keyed by writing system, and
/// <c>VernacularDefaultWritingSystem</c> is LibLCM's own accessor for "the one the project is written in",
/// rather than Motif re-deriving that choice from <c>WritingSystems.DefaultVernacularWritingSystem</c>
/// itself. A wordform with nothing recorded in that writing system contributes no string to hash and is
/// skipped rather than represented as an empty word.
/// </para>
/// <para>
/// <b>Streamed and cappable on purpose.</b> A mid-size project has about 7,000 word forms;
/// a larger project must not force every form
/// into memory before a caller can decide it only wants a bounded sample. <see cref="ExtractForms"/> yields
/// lazily from <c>IWfiWordformRepository.AllInstances()</c>, and <see cref="ExtractSelection"/> applies an
/// optional cap before anything is sorted or hashed.
/// </para>
/// </remarks>
public static class LcmWordformCorpus
{
    /// <summary>
    /// Every non-empty surface form in <paramref name="cache"/>'s default vernacular writing system, in
    /// whatever order the repository happens to enumerate them — <see cref="Selection.Create"/> is
    /// what imposes a deterministic order, deliberately not this method.
    /// </summary>
    public static IEnumerable<string> ExtractForms(LcmCache cache)
    {
        if (cache is null) throw new ArgumentNullException(nameof(cache));

        var repository = cache.ServiceLocator.GetInstance<IWfiWordformRepository>();
        foreach (var wordform in repository.AllInstances())
        {
            var form = wordform.Form.VernacularDefaultWritingSystem?.Text;
            if (!string.IsNullOrEmpty(form))
                yield return form;
        }
    }

    /// <summary>
    /// Builds a <see cref="Selection"/> from <paramref name="cache"/>'s wordforms.
    /// </summary>
    /// <param name="name">
    /// The label the resulting selection carries — see <see cref="Selection.Name"/>. Not
    /// derived from the cache automatically; a caller extracting a capped sample should say so here (for
    /// example <c>"my-project (first 1000)"</c>) rather than let the label imply the whole project.
    /// </param>
    /// <param name="limit">
    /// Caps how many forms are pulled from the live enumeration before sorting and hashing. Omit for the
    /// whole corpus; pass a small number for a quick check against a large project.
    /// </param>
    public static Selection ExtractSelection(LcmCache cache, string name, int? limit = null)
    {
        var forms = ExtractForms(cache);
        if (limit is int max) forms = forms.Take(max);
        return Selection.Create(name, forms);
    }
}
