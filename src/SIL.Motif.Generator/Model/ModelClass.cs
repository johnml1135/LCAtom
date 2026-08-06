namespace SIL.Motif.Generator.Model;

/// <summary>
/// One <c>&lt;class&gt;</c> declaration from <c>MasterLCModel.xml</c>. Only the three attributes the
/// generator actually consumes: <c>id</c> (the class name), <c>base</c> (its superclass, absent for
/// the root <c>CmObject</c>), and <c>abstract</c> — the fact the abstract-class rule checks
/// (ADR 0023 decision 2, <c>Checks/AbstractClassRule.cs</c>).
/// </summary>
public sealed record ModelClass(string Id, string? Base, bool Abstract);
