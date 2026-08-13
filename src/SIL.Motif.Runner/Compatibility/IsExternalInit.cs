namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// netstandard2.0 predates C#'s <c>init</c> accessors and <c>record</c> types; the compiler emits
    /// code referencing this type, which ships in the BCL on newer targets but not on netstandard2.0,
    /// so this project declares the marker itself. Compile-time only; no runtime behavior. Mirrors
    /// <c>src/SIL.Motif.Contract/Compatibility/IsExternalInit.cs</c> — each netstandard2.0 project
    /// needs its own copy because this trick relies on assembly-local visibility, not a shared reference.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
