namespace SIL.Motif.Generator.Model;

/// <summary>
/// The result of parsing <c>MasterLCModel.xml</c>: the declared <c>version</c> attribute plus every
/// class and field declaration.
/// </summary>
public sealed record ParsedModel(string Version, IReadOnlyList<ModelClass> Classes, IReadOnlyList<ModelField> Fields);
