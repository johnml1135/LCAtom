namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Moves every Corpus from one <see cref="ICorpusStore"/> into another, unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a migration path rather than dual-format reading.</b> A store already on disk under
/// <see cref="FileCorpusStore"/> stays exactly as readable as it always was — nothing about that format
/// changes, and it is never deleted by this method. What changes is that <see cref="SqliteCorpusStore"/> is the
/// engine <c>CorpusCommands</c> now points new work at, so existing corpora need one deliberate, run-once step
/// to appear there too. Reading both formats forever inside the same store type would mean carrying the file
/// layout's code indefinitely for a format nothing writes any more; a one-time import keeps that code exactly
/// as long as anyone still has files to bring across.
/// </para>
/// <para>
/// A corpus already present at the destination (by id) is left as it is there — this is an import, not a
/// mirror, so re-running it after the destination has moved on does not clobber newer work.
/// </para>
/// </remarks>
public static class CorpusStoreMigration
{
    /// <summary>Copies every corpus <paramref name="source"/> has into <paramref name="destination"/>.</summary>
    /// <returns>The ids that were actually copied — a corpus already present at the destination is skipped.</returns>
    public static IReadOnlyList<string> ImportInto(ICorpusStore source, ICorpusStore destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var imported = new List<string>();
        foreach (var corpusId in source.List())
        {
            if (destination.Exists(corpusId)) continue;

            var corpus = source.Load(corpusId);
            if (corpus is null) continue;   // listed but unreadable; nothing to carry across

            destination.Save(corpus);
            imported.Add(corpusId);
        }

        return imported;
    }
}
