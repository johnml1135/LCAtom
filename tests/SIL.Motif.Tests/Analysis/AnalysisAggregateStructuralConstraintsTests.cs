using System.Reflection;
using SIL.Motif.Host.Analysis;
using Xunit;

namespace SIL.Motif.Tests.Analysis;

/// <summary>
/// Structural pins for two of ADR 0038's constraints that a behavioural test alone cannot guarantee stays
/// true as the types evolve: that established/updated/removed can never be summed into one score, and that
/// nothing in this namespace offers a field implying which of two simultaneous changes caused the other.
/// </summary>
/// <remarks>
/// Both are checked by walking the public shape of the types with reflection rather than by example,
/// because the failure mode is a field someone adds later — a behavioural test that only exercises today's
/// fields would not catch that, while a shape check fails the moment the field appears.
/// </remarks>
public class AnalysisAggregateStructuralConstraintsTests
{
    private static readonly Type[] TypesUnderConstraint =
    {
        typeof(WordFormAnalysisAggregate),
        typeof(ApprovedAnalysis),
        typeof(AutomaticAnalysis),
        typeof(ManualAnalysisDiff),
        typeof(ManualAnalysisChange),
        typeof(AnalysisAggregateResponse),
        typeof(AnalysisAssessmentProvenance),
        typeof(UnanalysedReachFigure),
    };

    [Fact]
    public void NoTypeInTheAggregateExposesAFieldNamingACause()
    {
        // ADR 0038 decision 6: Motif reports facts side by side and declines to infer which caused which.
        var suspectWords = new[] { "Cause", "Caused", "Because", "Reason", "Why", "Attribut", "DueTo" };

        foreach (var type in TypesUnderConstraint)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var word in suspectWords)
                {
                    Assert.False(
                        property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                        $"{type.Name}.{property.Name} reads as a causal-attribution field, which ADR 0038 " +
                        "decision 6 forbids: Motif reports facts side by side and never infers which caused which.");
                }
            }
        }
    }

    [Fact]
    public void ManualAnalysisDiff_HasNoNumericOrBooleanFieldThatCouldActAsANetScore()
    {
        // ADR 0038 decision 5: established, updated and removed are reported separately and never netted.
        var numericOrBooleanProperties = typeof(ManualAnalysisDiff)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)
                                                        || p.PropertyType == typeof(double))
            .ToList();

        Assert.Empty(numericOrBooleanProperties);
    }

    [Fact]
    public void ManualAnalysisDiff_ExposesExactlyTheFourSeparateCategories()
    {
        var listProperties = typeof(ManualAnalysisDiff)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "Established", "Removed", "Updated", "Vanished" }.OrderBy(n => n, StringComparer.Ordinal),
            listProperties);
    }
}
