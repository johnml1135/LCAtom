namespace SIL.Motif.Generator.Manifest;

/// <summary>
/// One row of <c>manifest/liblcm-inventory.tsv</c>, all 18 columns (manifest/README.md). Values stay
/// as the raw (unquoted) strings the TSV carries — <c>Scope != in</c> rows leave most policy columns
/// blank, and modelling that as <see cref="string.Empty"/> rather than parsing into stricter types
/// keeps <see cref="ManifestTsvParser"/> a pure, lossless transcription with no row-shape judgement
/// of its own.
/// </summary>
/// <remarks>
/// The column named <see cref="Group"/> here is the manifest's literal TSV header. Per
/// ADR 0024 the concept it holds was renamed
/// <em>domain</em> — "the editorial domain: who should review a change" — precisely so it stops
/// being confused with the kind's derived first segment, which this project also calls "group"
/// (<c>Derivation/GroupDerivation.cs</c>). The TSV file itself is read-only (manifest/README.md) and
/// was not renamed; this record keeps the TSV's own header name so a reader can grep one term across
/// both the file and this parser.
/// </remarks>
public sealed record ManifestRow(
    string Class,
    string Base,
    string Abstract,
    string Scope,
    string ScopeReason,
    string Field,
    string Kind,
    string Sig,
    string Card,
    string HcReferenced,
    string Construct,
    string Group,
    string Classification,
    string ComparisonClass,
    string Verbs,
    string HcReachable,
    string EnumValues,
    string Rationale);
