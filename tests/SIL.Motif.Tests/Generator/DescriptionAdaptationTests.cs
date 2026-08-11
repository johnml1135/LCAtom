using SIL.Motif.Generator;
using SIL.Motif.Generator.Descriptions;
using SIL.Motif.Generator.Descriptions.Harvest;
using Xunit;

namespace SIL.Motif.Tests.Generator;

/// <summary>
/// An adapted description is the one place this project derives prose from a source that describes a
/// <em>different</em> field. What makes that safe is that the substitution is re-applied to the sibling's
/// current text on every refresh and fails when it no longer fits — so an adapted row cannot outlive the
/// sentence it was adapted from. These tests are about that failure.
/// </summary>
public class DescriptionAdaptationTests
{
    private const string SiblingText =
        "Allows the user to write phonological rules that are sensitive to a particular phoneme " +
        "(as opposed to a class of phonemes).";

    private static DescriptionAdaptations.Rule RuleFor(string cls) =>
        DescriptionAdaptations.Rules.Single(r => r.Class == cls);

    [Fact]
    public void TheBoundaryRule_SubstitutesOnlyTheClauseThatDiffers()
    {
        Assert.Equal(
            "Allows the user to write phonological rules that are sensitive to a particular boundary marker " +
            "(as opposed to a phoneme or a class of phonemes).",
            DescriptionAdaptations.Apply(RuleFor("PhSimpleContextBdry"), SiblingText));
    }

    [Fact]
    public void TheNaturalClassRule_SubstitutesOnlyTheClauseThatDiffers()
    {
        Assert.Equal(
            "Allows the user to write phonological rules that are sensitive to a natural class of phonemes " +
            "(as opposed to one particular phoneme).",
            DescriptionAdaptations.Apply(RuleFor("PhSimpleContextNC"), SiblingText));
    }

    /// <summary>
    /// The check the research pass asked for and could not express as prose. A copied-and-edited sentence
    /// would survive this rewording silently, still cited, still reading fluently.
    /// </summary>
    [Fact]
    public void AReworddedSiblingSentence_FailsTheAdaptationRatherThanProducingSomething()
    {
        const string reworded =
            "Allows the user to write phonological rules sensitive to one specific segment, rather than to " +
            "a group of them.";

        var ex = Assert.Throws<GeneratorException>(
            () => DescriptionAdaptations.Apply(RuleFor("PhSimpleContextNC"), reworded));

        Assert.Contains("PhSimpleContextNC.FeatureStructure", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no longer appears", ex.Message, StringComparison.Ordinal);
        // The failure quotes the new wording, so whoever fixes the rule can read it without opening the model.
        Assert.Contains(reworded, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ambiguity is a failure too: with two occurrences the rule no longer decides which one it means, and
    /// silently taking the first is how a substitution starts producing a sentence nobody wrote.
    /// </summary>
    [Fact]
    public void AClauseThatNowOccursTwice_FailsRatherThanSubstitutingTheFirst()
    {
        var ambiguous = SiblingText + " " + SiblingText;

        var ex = Assert.Throws<GeneratorException>(
            () => DescriptionAdaptations.Apply(RuleFor("PhSimpleContextBdry"), ambiguous));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRule_NamesASiblingThatIsADifferentFieldFromItsTarget()
    {
        Assert.All(DescriptionAdaptations.Rules, rule => Assert.NotEqual(rule.Key, rule.SourceKey));
        Assert.All(DescriptionAdaptations.Rules, rule => Assert.NotEqual(rule.Find, rule.Replace));
        Assert.All(DescriptionAdaptations.Rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Licence)));
    }

    /// <summary>
    /// The consistency this design turns on, checked against the shipped file and needing no upstream source:
    /// an adapted row is pinned to its <em>sibling's</em> fragment digest, so if the sibling's sentence is
    /// ever re-harvested and the adapted row is not, the two stop matching and this fails.
    /// </summary>
    [Fact]
    public void EveryAdaptedRow_CarriesItsSiblingsSourceHash()
    {
        var descriptions = KindDescriptionTsvParser.Parse(RepoPaths.DefaultDescriptionsPath())
            .ToDictionary(d => (d.Class, d.Field));

        foreach (var rule in DescriptionAdaptations.Rules)
        {
            var adapted = descriptions[(rule.Class, rule.Field)];
            var sibling = descriptions[(rule.SourceClass, rule.SourceField)];

            Assert.Equal(DescriptionAdaptations.ReviewedValue, adapted.Reviewed);
            Assert.Equal(sibling.SourceHash, adapted.SourceHash);
            Assert.False(string.IsNullOrWhiteSpace(adapted.SourceHash));

            // And the row really is the substitution applied to the sibling's shipped text -- so the file
            // cannot hold a hand-edited sentence wearing an adapted label.
            Assert.Equal(DescriptionAdaptations.Apply(rule, sibling.Description), adapted.Description);
        }
    }
}
