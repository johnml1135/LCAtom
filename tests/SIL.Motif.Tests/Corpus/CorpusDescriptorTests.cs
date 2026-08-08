using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Unit tests for the pure hashing/ordering half of the corpus descriptor — no <c>LcmCache</c>, no parser,
/// just a word list in and a deterministic hash out. See
/// <see cref="SIL.Motif.Tests.Corpus.LcmWordformCorpusTests"/> for the part that needs a real project.
/// </summary>
public class CorpusDescriptorTests
{
    [Fact]
    public void Create_SortsWordsOrdinally_RegardlessOfInputOrder()
    {
        var descriptor = CorpusDescriptor.Create("test-corpus", new[] { "nkazi", "anthu", "mbali" });

        Assert.Equal(new[] { "anthu", "mbali", "nkazi" }, descriptor.Words);
    }

    [Fact]
    public void Create_SameWordsInADifferentInputOrder_ProduceTheSameHash()
    {
        var first = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya", "nkazi" });
        var second = CorpusDescriptor.Create("test-corpus", new[] { "nkazi", "mbali", "ya" });

        // The whole point of sorting before hashing: two extractions of the same wordforms that merely
        // enumerated them differently must not look like drift.
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Words, second.Words);
    }

    [Fact]
    public void Create_HashChangesWhenTheWordListChanges()
    {
        var original = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya", "nkazi" });
        var withOneMoreWord = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya", "nkazi", "munthu" });
        var withOneWordRemoved = CorpusDescriptor.Create("test-corpus", new[] { "mbali", "ya" });

        Assert.NotEqual(original.Sha256, withOneMoreWord.Sha256);
        Assert.NotEqual(original.Sha256, withOneWordRemoved.Sha256);
    }

    [Fact]
    public void Create_HashIsStableAcrossRepeatedCallsWithIdenticalInput()
    {
        var words = new[] { "mbali", "ya", "nkazi" };

        var first = CorpusDescriptor.Create("test-corpus", words);
        var second = CorpusDescriptor.Create("test-corpus", words);

        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public void Create_HashDoesNotDependOnTheCorpusId()
    {
        // The id is a label for reporting; the hash is a fact about content. Two differently-labelled
        // descriptors over the same words describe the same corpus content, which a reviewer comparing
        // hashes across a rename needs to be true.
        var first = CorpusDescriptor.Create("sena-3", new[] { "mbali", "ya" });
        var second = CorpusDescriptor.Create("a different label", new[] { "mbali", "ya" });

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.CorpusId, second.CorpusId);
    }

    [Fact]
    public void Create_HashIsPrefixedLikeOtherDigestsInThisCodebase()
    {
        var descriptor = CorpusDescriptor.Create("test-corpus", new[] { "mbali" });

        Assert.StartsWith("sha256:", descriptor.Sha256);
        Assert.Equal("sha256:".Length + 64, descriptor.Sha256.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsAMissingCorpusId(string? corpusId)
    {
        Assert.Throws<ArgumentException>(() => CorpusDescriptor.Create(corpusId!, new[] { "mbali" }));
    }

    [Fact]
    public void Create_RejectsNullWords()
    {
        Assert.Throws<ArgumentNullException>(() => CorpusDescriptor.Create("test-corpus", null!));
    }

    [Fact]
    public void Create_AcceptsAnEmptyCorpus()
    {
        // An empty corpus is a legitimate (if useless) input — this exercises the boundary rather than
        // asserting it should be rejected, since nothing about hashing or ordering requires words to exist.
        var descriptor = CorpusDescriptor.Create("empty-corpus", Array.Empty<string>());

        Assert.Empty(descriptor.Words);
        Assert.StartsWith("sha256:", descriptor.Sha256);
    }

    [Fact]
    public void ACorpusIsASet_SoDuplicatesDoNotChangeItsIdentity()
    {
        // Found reviewing the first implementation, which sorted but did not deduplicate. CoverageFigure
        // compares a corpus against what was analysed with set semantics, and a word form analysed twice is
        // analysed once — so if the hash counted duplicates, two corpora covering identically the same words
        // would carry different hashes and a figure would report drift against itself.
        var once = CorpusDescriptor.Create("sena", new[] { "mbali", "ya", "miseru" });
        var twice = CorpusDescriptor.Create("sena", new[] { "mbali", "ya", "mbali", "miseru", "ya" });

        Assert.Equal(once.Sha256, twice.Sha256);
        Assert.Equal(once.Words, twice.Words);
        Assert.Equal(3, twice.Words.Count);
    }
}
