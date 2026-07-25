using System.Runtime.CompilerServices;
using SIL.LCAtom.Contract.Parsing;

namespace SIL.LCAtom.Runner.Operations;

/// <summary>
/// Names the one Stage C operation kind: setting a <c>LexSense</c>'s <c>Gloss</c> for a writing
/// system. See docs/change-set-contract.md, "Operation vocabulary" ("set or clear a
/// writing-system alternative").
/// </summary>
public static class LexicalSenseOperationKinds
{
    public const string SetGloss = "lexical/sense/setGloss";

    /// <summary>
    /// Registers <see cref="SetGloss"/> with the LibLCM-free <see cref="OperationKindRegistry"/>.
    /// </summary>
    /// <remarks>
    /// A module initializer, not a static constructor triggered by first touch: a <c>const</c>
    /// field like <see cref="SetGloss"/> is inlined by the compiler at every call site and never
    /// forces this type's static constructor to run, so registration could otherwise depend on
    /// accidental ordering — e.g. whether a test calls <c>ChangeSetJsonParser.Parse</c> (which
    /// needs the kind already registered) before anything else in this assembly happens to touch
    /// this type. A <see cref="ModuleInitializerAttribute"/> method runs once, unconditionally, as
    /// soon as this assembly's module loads into the process — before any of its other code runs —
    /// so registration is guaranteed regardless of call order.
    /// </remarks>
    [ModuleInitializer]
    internal static void Register()
    {
        OperationKindRegistry.Register(SetGloss);
    }
}
