using System.Collections.Generic;
using SIL.Motif.Host.Analysis;

namespace SIL.Motif.Host.Store;

/// <summary>Where Assessments are kept, once something has produced one.</summary>
/// <remarks>
/// <para>
/// ADR 0036 decision 6 puts Assessments in the embedded database alongside Corpora, because a report over one
/// — "how many word forms does this cover", "the hundred most frequent unparsed forms" — is required to be
/// exceptionally cheap and a file gives that no aggregate query. Producing a <see cref="StoredAssessment"/> is
/// unchanged and stays outside this type: <c>PanGlossParser</c> and <see cref="Analysis.AnalysisAggregateReader"/>
/// know nothing about where one is kept, exactly as ADR 0038 decision 5 requires.
/// </para>
/// <para>
/// <b>Identity is content, not a caller-supplied name.</b> Unlike a Corpus, whose id is chosen by whoever
/// creates it, an Assessment's id is derived from what it says — see <see cref="AssessmentIdentity.ComputeId"/>
/// — so re-saving the literal same parser run against the literal same corpus and grammar collapses onto the
/// row already there rather than accumulating a duplicate. This matches the rest of the store's principle that
/// everything in it is either cached or re-fetchable (ADR 0036 consequences).
/// </para>
/// <para>
/// <b>Pinning and pruning.</b> <see cref="Pin"/>/<see cref="Unpin"/> record that something — in practice a
/// Proposal citing this Assessment as evidence — currently depends on it, and <see cref="ListUnpinnedAssessmentIds"/>
/// is the query that finds Assessments nothing currently pins. Neither this type nor any caller in this
/// codebase deletes an unpinned Assessment or runs that query on a schedule: how an Assessment is pruned once
/// nothing pins it is one of ADR 0036's open questions, and this is deliberately the mechanism without the
/// policy. Nothing in this codebase calls <see cref="Pin"/> yet either, because the thing that would call it —
/// a Proposal citing an Assessment as evidence — is not built.
/// </para>
/// </remarks>
public interface IAssessmentStore
{
    /// <summary>Whether an Assessment with this id is already stored.</summary>
    bool Exists(string assessmentId);

    /// <summary>Load a stored Assessment in full, or <c>null</c> if there is none with this id.</summary>
    StoredAssessment? Load(string assessmentId);

    /// <summary>Write an Assessment, replacing what is there for the same id. Returns the id it was saved under.</summary>
    string Save(StoredAssessment assessment);

    /// <summary>Every stored Assessment id, in a stable order.</summary>
    IReadOnlyList<string> List();

    /// <summary>How many word forms this Assessment covers, without reading any of them.</summary>
    int CountWords(string assessmentId);

    /// <summary>How many analyses this Assessment recorded in total, without reading any of them.</summary>
    int CountAnalyses(string assessmentId);

    /// <summary>Records that <paramref name="pinnedBy"/> currently depends on this Assessment.</summary>
    void Pin(string assessmentId, string pinnedBy);

    /// <summary>Remove a previously recorded dependency. Unpinning something never pinned is a no-op.</summary>
    void Unpin(string assessmentId, string pinnedBy);

    /// <summary>Every Assessment id with zero recorded pins, in a stable order. Finds candidates; deletes nothing.</summary>
    IReadOnlyList<string> ListUnpinnedAssessmentIds();
}
