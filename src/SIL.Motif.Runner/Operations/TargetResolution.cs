using System;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Runner.Resolution;
using SIL.LCModel;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// The resolve-and-type-check preamble every generated handler needs before it can read or lower a
/// field — generalized from <c>SetGlossOperationHandler</c>'s body, which inlined exactly
/// this sequence for <c>ILexSense</c> alone: require <c>target</c>, resolve it through
/// <see cref="CanonicalIdResolver"/>, and fail with the same wording that type checked for
/// <c>ILexSense</c> ("Target '...' is not a LexSense (it is a ...).") for any target class.
/// </summary>
internal static class TargetResolution
{
    /// <param name="kind">Named in the "requires 'target'" error only.</param>
    public static (CanonicalId Id, T Object) Resolve<T>(LcmCache cache, OperationEnvelope operation, string kind)
        where T : ICmObject
    {
        if (operation.Target is not { } targetId)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId.Value}' of kind '{kind}' requires 'target'.");
        }

        var resolved = CanonicalIdResolver.Resolve(cache, targetId);
        if (resolved is not T typed)
        {
            // typeof(T).Name is "I" + the LibLCM class name for every target interface here; trim it for messages.
            var expectedName = typeof(T).Name.StartsWith("I", StringComparison.Ordinal)
                ? typeof(T).Name.Substring(1)
                : typeof(T).Name;
            throw new InvalidOperationException(
                $"Target '{targetId.Value}' is not a {expectedName} (it is a {resolved.GetType().Name}).");
        }

        return (targetId, typed);
    }
}
