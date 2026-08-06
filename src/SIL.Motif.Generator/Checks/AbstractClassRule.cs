using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Checks;

/// <summary>
/// ADR 0023 decision 2: "<c>create</c> and <c>delete</c> name a concrete class; every other verb
/// names the declaring class." The kind's <em>construct</em> segment is always
/// <c>lowerFirst(DeclaringClass)</c> with no exception (<see cref="Derivation.ConstructDerivation"/>)
/// — what this rule actually checks is the object a <c>create</c>/<c>delete</c> would instantiate or
/// destroy, which for an owning field is <see cref="ModelField.Sig"/>, not
/// <see cref="ModelField.DeclaringClass"/>. <c>LexEntry.LexemeForm</c> (owning/atomic, <c>Sig =
/// MoForm</c>) is the live example the plan names: <c>MoForm</c> is <c>abstract="true"</c>
/// (MasterLCModel.xml), so nothing of that exact type can ever be constructed — only a concrete
/// subclass such as <c>MoStemAllomorph</c> can. Generating a "create a MoForm" kind would describe
/// an object that cannot exist, so it fails here rather than surfacing as a runtime error the first
/// time an agent tries to author one.
/// </summary>
/// <remarks>
/// This rule is deliberately <b>not</b> wired into <see cref="ManifestConsistencyChecker"/>'s
/// whole-manifest pass. Doing so today would fail the build on real, currently in-scope data
/// (<c>LexEntry.LexemeForm</c> and <c>LexEntry.AlternateForms</c>, both <c>Sig = MoForm</c>) before
/// MOT-4 has decided how those two fields resolve to concrete-subclass kinds instead — a decision
/// this generator skeleton does not make (docs/plan-motif.md MOT-3: "emits no code yet"). It is
/// exposed here, fully tested against the real <c>MoForm</c> fixture, for MOT-4 to call once it
/// actually emits a kind per field.
/// </remarks>
public static class AbstractClassRule
{
    /// <param name="declaringClass">For the message only.</param>
    /// <param name="fieldName">For the message only.</param>
    /// <param name="verb">Only <c>create</c>/<c>delete</c> are checked; every other verb passes
    /// unconditionally, since only those two verbs instantiate or destroy an object of
    /// <paramref name="targetClass"/>'s type.</param>
    /// <param name="targetClass">The class the field's <c>Sig</c> resolves to — the object a
    /// <c>create</c>/<c>delete</c> kind would act on.</param>
    public static void Check(string declaringClass, string fieldName, string verb, ModelClass targetClass)
    {
        if (verb is not ("create" or "delete")) return;

        if (targetClass.Abstract)
            throw new GeneratorException(
                $"{declaringClass}.{fieldName}: a '{verb}' kind would target '{targetClass.Id}', which is " +
                "abstract — no such object can exist (ADR 0023 decision 2).");
    }
}
