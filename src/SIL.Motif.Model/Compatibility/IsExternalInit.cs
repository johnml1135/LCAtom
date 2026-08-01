// netstandard2.0 predates C#'s `init` accessors and `record` types. The compiler emits code that
// references System.Runtime.CompilerServices.IsExternalInit; on newer target frameworks that type
// ships in the BCL, but netstandard2.0 has no such type, so we declare the standard empty marker
// ourselves. This is a compile-time-only marker with no runtime behavior. Mirrors
// src/SIL.Motif.Contract/Compatibility/IsExternalInit.cs — each netstandard2.0 project needs its
// own copy because this trick relies on assembly-local visibility, not a shared reference.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
