using SIL.Motif.Generator.Checks;
using SIL.Motif.Generator.Coverage;
using SIL.Motif.Generator.Join;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;
using SIL.Motif.Generator.ModelSource;

namespace SIL.Motif.Generator;

/// <summary>The full result of one load: where the model came from, the parsed model, the joined
/// rows, and the model coverage report — everything a caller needs in order to inspect what was
/// loaded.</summary>
public sealed record LoadedMotifModel(
    ModelPathResult PathResult,
    ParsedModel Model,
    IReadOnlyList<JoinedRow> Rows,
    ModelCoverageReport Coverage);

/// <summary>
/// The generator's single entry point: resolve <c>MasterLCModel.xml</c>, parse it, parse the
/// manifest, join the two fail-closed, run the derivation-consistency checks ADR 0022/0024
/// require, and report model coverage. It emits nothing — turning
/// <see cref="LoadedMotifModel.Rows"/> into operation code is a separate concern this loader does
/// not take on.
/// </summary>
public static class MotifModelLoader
{
    /// <param name="manifestPath">Defaults to <c>manifest/liblcm-inventory.tsv</c> under the repo
    /// root (<see cref="RepoPaths.DefaultManifestPath"/>). Overridable so tests can point at a
    /// fixture with an injected extra/missing/mismatched row without touching the read-only real
    /// file (manifest/README.md: "never modify it").</param>
    /// <param name="packagesRootOverride">Forwarded to <see cref="ModelPathResolver.Resolve"/>.</param>
    /// <param name="libLcmCheckoutRoot">Forwarded to <see cref="ModelPathResolver.Resolve"/>.</param>
    public static LoadedMotifModel Load(
        string? manifestPath = null,
        string? packagesRootOverride = null,
        string? libLcmCheckoutRoot = null)
    {
        var pathResult = ModelPathResolver.Resolve(packagesRootOverride, libLcmCheckoutRoot);
        var model = MasterLcModelParser.Parse(pathResult.Path);

        var resolvedManifestPath = manifestPath ?? RepoPaths.DefaultManifestPath();
        var manifestRows = ManifestTsvParser.Parse(resolvedManifestPath);

        var rows = ModelManifestJoiner.Join(model.Fields, manifestRows);

        // ADR 0022 decision 3 and ADR 0024 decision 2: both checks fail closed, naming the offending row/class, before a report that looks like everything is fine could be printed.
        ManifestConsistencyChecker.CheckVerbsAndComparisonClass(rows);
        ManifestConsistencyChecker.CheckGroupIsTotal(rows);

        var modelCoverage = ModelCoverageReportBuilder.Build(pathResult, model, rows);
        return new LoadedMotifModel(pathResult, model, rows, modelCoverage);
    }
}
