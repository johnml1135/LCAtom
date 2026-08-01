#if NETSTANDARD2_0

// ReSharper disable once CheckNamespace — the compiler recognises this attribute by full name only.
namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill for the framework attribute of the same name, which does not exist in
/// <c>netstandard2.0</c>.
/// </summary>
/// <remarks>
/// The Runner multi-targets <c>netstandard2.0;net10.0</c> so it can load in-process in FieldWorks
/// while FieldWorks is still <c>net48</c> (see AGENTS.md, "Compatibility targets"). Module
/// initializers are a compiler feature, not a runtime one: Roslyn emits the <c>.cctor</c> on the
/// module regardless of target framework, and only needs a type with this exact full name to be
/// resolvable. Declaring it here is the standard polyfill and changes no behaviour on
/// <c>net10.0</c>, where this file compiles to nothing.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class ModuleInitializerAttribute : Attribute;

#endif
