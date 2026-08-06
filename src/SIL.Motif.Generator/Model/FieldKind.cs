namespace SIL.Motif.Generator.Model;

/// <summary>
/// The three field shapes <c>MasterLCModel.xml</c> declares, one element name each:
/// <c>&lt;basic&gt;</c> (a value), <c>&lt;owning&gt;</c>, and <c>&lt;rel&gt;</c> (a reference).
/// Matches the manifest's <c>Kind</c> column (manifest/README.md).
/// </summary>
public enum FieldKind
{
    Basic,
    Owning,
    Rel,
}
