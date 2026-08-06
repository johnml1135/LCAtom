namespace SIL.Motif.Generator.Derivation;

/// <summary>
/// The kind's middle segment: <c>lowerFirst(DeclaringClass)</c>, verbatim, no prefix stripping
/// (ADR 0023 decision 1). Deliberately not the manifest's hand-authored <c>Construct</c> column —
/// that column now means "what ships together" (a staging grouping; <c>featureStructure</c> spans 16
/// classes) and is a different question, so it is never compared against this
/// (docs/plan-motif.md MOT-2, ADR 0023 decision 3).
/// </summary>
public static class ConstructDerivation
{
    public static string Derive(string declaringClass)
    {
        if (string.IsNullOrEmpty(declaringClass))
            throw new GeneratorException("Cannot derive a construct segment from an empty declaring class name.");

        return char.ToLowerInvariant(declaringClass[0]) + declaringClass[1..];
    }
}
