using SIL.Motif.Generator;
using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Manifest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// The kind's first segment: a closed table over the declaring class's name prefix (ADR 0024
/// decision 2). This is deliberately never checked against the manifest's own <c>Group</c>/domain
/// column — see the class-level remarks on <c>GroupDerivation</c> — so these tests pin only the
/// derivation's own behavior: known examples, and totality over every class actually present.
/// </summary>
public class GroupDerivationTests
{
    [Theory]
    [InlineData("LexEntry", "lexical")]
    [InlineData("LexSense", "lexical")]
    [InlineData("MoForm", "grammar")]
    [InlineData("PhSegRuleRHS", "grammar")]
    [InlineData("FsFeatureSystem", "grammar")]
    [InlineData("CmPossibility", "lists")]
    [InlineData("CmPossibilityList", "lists")]
    [InlineData("CmAgent", "analysis")]
    [InlineData("WfiWordform", "analysis")]
    [InlineData("Segment", "analysis")]
    [InlineData("Text", "analysis")]
    [InlineData("CmFolder", "system")] // an ordinary Cm* class, not one of the analysis overrides
    public void Derive_KnownClasses_ProducesExpectedGroup(string declaringClass, string expectedGroup)
    {
        Assert.Equal(expectedGroup, GroupDerivation.Derive(declaringClass));
    }

    [Fact]
    public void Derive_UnknownPrefix_FailsNamingTheClass()
    {
        var ex = Assert.Throws<GeneratorException>(() => GroupDerivation.Derive("ZzzUnrecognizedSyntheticClass"));
        Assert.Contains("ZzzUnrecognizedSyntheticClass", ex.Message);
    }

    [Fact]
    public void Derive_IsTotalOverEveryDeclaringClassInTheRealManifest()
    {
        // ADR 0024 decision 2: this table must be total, so every declaring class (~180) must resolve.
        var classes = ManifestTsvParser.Parse(RepoPaths.DefaultManifestPath())
            .Select(r => r.Class)
            .Distinct()
            .ToList();

        Assert.NotEmpty(classes);

        var unresolved = new List<string>();
        foreach (var cls in classes)
        {
            try
            {
                GroupDerivation.Derive(cls);
            }
            catch (GeneratorException)
            {
                unresolved.Add(cls);
            }
        }

        Assert.Empty(unresolved);
    }
}
