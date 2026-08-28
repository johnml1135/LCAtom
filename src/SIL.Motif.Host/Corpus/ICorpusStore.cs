using System.Collections.Generic;

namespace SIL.Motif.Host.Corpus;

/// <summary>Where Corpora are kept.</summary>
/// <remarks>
/// An interface so <see cref="CorpusIngestion"/> depends on Corpus storage behaviour rather than on
/// <see cref="SqliteCorpusStore"/> directly, which is its only implementation.
/// </remarks>
public interface ICorpusStore
{
    /// <summary>Whether a corpus with this id is already stored.</summary>
    bool Exists(string corpusId);

    /// <summary>Load a corpus with its documents, or <c>null</c> if there is none.</summary>
    StoredCorpus? Load(string corpusId);

    /// <summary>Write a corpus, replacing what is there.</summary>
    void Save(StoredCorpus corpus);

    /// <summary>Every stored corpus id, in a stable order.</summary>
    IReadOnlyList<string> List();
}
