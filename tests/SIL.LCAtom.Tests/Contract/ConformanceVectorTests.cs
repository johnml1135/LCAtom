using System;
using System.IO;
using System.Text;
using SIL.LCAtom.Contract.Canonicalization;
using SIL.LCAtom.Contract.Parsing;
using Xunit;

namespace SIL.LCAtom.Tests.Contract;

/// <summary>
/// Loads the frozen, language-agnostic conformance vectors under <c>tests/conformance/change-set-digest</c>
/// (input Change Set JSON → expected RFC 8785 canonical bytes → expected intent digest) and checks
/// this C# implementation reproduces them exactly. A Python or Rust runner is expected to load the
/// same <c>input.json</c> files and reproduce the same <c>canonical.json</c> bytes and
/// <c>digest.txt</c> value, per docs/adr/0007-cross-language-digest-determinism.md.
/// </summary>
public class ConformanceVectorTests
{
    public static TheoryData<string> VectorDirectories()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(FindConformanceRoot()))
            data.Add(dir);
        return data;
    }

    [Theory]
    [MemberData(nameof(VectorDirectories))]
    public void Vector_CanonicalBytesAndDigest_MatchFrozenExpectation(string vectorDir)
    {
        var inputJson = File.ReadAllText(Path.Combine(vectorDir, "input.json"));
        var expectedCanonical = File.ReadAllText(Path.Combine(vectorDir, "canonical.json"));
        var expectedDigest = File.ReadAllText(Path.Combine(vectorDir, "digest.txt"));

        var changeSet = ChangeSetJsonParser.Parse(inputJson);
        var canonicalBytes = IntentDigest.CanonicalBytes(changeSet);
        var actualCanonical = Encoding.UTF8.GetString(canonicalBytes);
        var actualDigest = IntentDigest.Sha256Of(canonicalBytes);

        Assert.Equal(expectedCanonical, actualCanonical);
        Assert.Equal(expectedDigest, actualDigest);
    }

    [Fact]
    public void AtLeastTwoVectorsExist()
    {
        var count = Directory.GetDirectories(FindConformanceRoot()).Length;
        Assert.True(count >= 2, $"Expected at least two frozen conformance vectors, found {count}.");
    }

    /// <summary>
    /// Locates <c>tests/conformance/change-set-digest</c> as a sibling of this test assembly's repo
    /// checkout, the same directory-walk-up technique <c>ProjectLoadTests</c> uses to find the
    /// FieldWorks fixture.
    /// </summary>
    private static string FindConformanceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "LCAtom")
            dir = dir.Parent;

        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate the LCAtom repo root from the test assembly location.");
        }

        var conformanceRoot = Path.Combine(dir.FullName, "tests", "conformance", "change-set-digest");
        if (!Directory.Exists(conformanceRoot))
            throw new InvalidOperationException($"Expected conformance vectors at '{conformanceRoot}'.");

        return conformanceRoot;
    }
}
