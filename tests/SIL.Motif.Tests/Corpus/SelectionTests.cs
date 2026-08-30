using SIL.Motif.Host.Corpus;
using Xunit;

namespace SIL.Motif.Tests.Corpus;

/// <summary>
/// Unit tests for the pure hashing/ordering half of a Selection — no <c>LcmCache</c>, no parser,
/// just a word list in and a deterministic hash out. See
/// <see cref="SIL.Motif.Tests.Corpus.LcmWordformCorpusTests"/> for the part that needs a real project.
/// </summary>
public class SelectionTests
{
    [Fact]
    public void Create_SortsWordsOrdinally_RegardlessOfInputOrder()
    {
        var selection = Selection.Create("test-corpus", new[] { "nkazi", "anthu", "mbali" });

        Assert.Equal(new[] { "anthu", "mbali", "nkazi" }, selection.Words);
    }

    [Fact]
    public void Create_SameWordsInADifferentInputOrder_ProduceTheSameHash()
    {
        var first = Selection.Create("test-corpus", new[] { "mbali", "ya", "nkazi" });
        var second = Selection.Create("test-corpus", new[] { "nkazi", "mbali", "ya" });

        // The whole point of sorting before hashing: differently-enumerated same wordforms must not look like drift.
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Words, second.Words);
    }

    [Fact]
    public void Create_HashChangesWhenTheWordListChanges()
    {
        var original = Selection.Create("test-corpus", new[] { "mbali", "ya", "nkazi" });
        var withOneMoreWord = Selection.Create("test-corpus", new[] { "mbali", "ya", "nkazi", "munthu" });
        var withOneWordRemoved = Selection.Create("test-corpus", new[] { "mbali", "ya" });

        Assert.NotEqual(original.Sha256, withOneMoreWord.Sha256);
        Assert.NotEqual(original.Sha256, withOneWordRemoved.Sha256);
    }

    [Fact]
    public void Create_HashIsStableAcrossRepeatedCallsWithIdenticalInput()
    {
        var words = new[] { "mbali", "ya", "nkazi" };

        var first = Selection.Create("test-corpus", words);
        var second = Selection.Create("test-corpus", words);

        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public void Create_HashDoesNotDependOnTheName()
    {
        // The name is a label; the hash is a content fact — differently-labelled selections over the same words match.
        var first = Selection.Create("sena-3", new[] { "mbali", "ya" });
        var second = Selection.Create("a different label", new[] { "mbali", "ya" });

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public void Create_HashIsPrefixedLikeOtherDigestsInThisCodebase()
    {
        var selection = Selection.Create("test-corpus", new[] { "mbali" });

        Assert.StartsWith("sha256:", selection.Sha256);
        Assert.Equal("sha256:".Length + 64, selection.Sha256.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsAMissingName(string? name)
    {
        Assert.Throws<ArgumentException>(() => Selection.Create(name!, new[] { "mbali" }));
    }

    [Fact]
    public void Create_RejectsNullWords()
    {
        Assert.Throws<ArgumentNullException>(() => Selection.Create("test-corpus", null!));
    }

    [Fact]
    public void Create_AcceptsAnEmptyCorpus()
    {
        // An empty selection is a legitimate (if useless) input; hashing and ordering do not require words to exist.
        var selection = Selection.Create("empty-corpus", Array.Empty<string>());

        Assert.Empty(selection.Words);
        Assert.StartsWith("sha256:", selection.Sha256);
    }

    [Fact]
    public void ACorpusIsASet_SoDuplicatesDoNotChangeItsIdentity()
    {
        // GrammarCoverageFigure compares with set semantics; a duplicate-counting hash would fake drift.
        var once = Selection.Create("sena", new[] { "mbali", "ya", "miseru" });
        var twice = Selection.Create("sena", new[] { "mbali", "ya", "mbali", "miseru", "ya" });

        Assert.Equal(once.Sha256, twice.Sha256);
        Assert.Equal(once.Words, twice.Words);
        Assert.Equal(3, twice.Words.Count);
    }
}
