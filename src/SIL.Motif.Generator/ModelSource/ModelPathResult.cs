namespace SIL.Motif.Generator.ModelSource;

/// <summary>The resolved <c>MasterLCModel.xml</c> path, plus which of the two candidate locations
/// produced it (see <see cref="ModelPathSource"/>).</summary>
public sealed record ModelPathResult(string Path, ModelPathSource Source);
