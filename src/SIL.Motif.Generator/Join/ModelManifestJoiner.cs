using System.Text;
using SIL.Motif.Generator.Manifest;
using SIL.Motif.Generator.Model;

namespace SIL.Motif.Generator.Join;

/// <summary>
/// Joins <c>MasterLCModel.xml</c>'s fields to the manifest's rows on
/// <c>(Class, Field)</c>, and fails closed — naming every offending key — on any key present on one
/// side and absent from the other, or duplicated on either side. The two sides balance exactly
/// (898 = 898); this class is what keeps that true as LibLCM and the
/// manifest evolve independently, rather than something checked once by hand.
/// </summary>
public static class ModelManifestJoiner
{
    public static IReadOnlyList<JoinedRow> Join(IReadOnlyList<ModelField> modelFields, IReadOnlyList<ManifestRow> manifestRows)
    {
        var modelByKey = new Dictionary<FieldKey, ModelField>();
        foreach (var field in modelFields)
        {
            var key = new FieldKey(field.DeclaringClass, field.FieldName);
            if (!modelByKey.TryAdd(key, field))
                throw new GeneratorException($"MasterLCModel.xml declares '{key}' more than once.");
        }

        var manifestByKey = new Dictionary<FieldKey, ManifestRow>();
        foreach (var row in manifestRows)
        {
            var key = new FieldKey(row.Class, row.Field);
            if (!manifestByKey.TryAdd(key, row))
                throw new GeneratorException($"The manifest lists '{key}' more than once.");
        }

        var modelOnly = modelByKey.Keys.Where(key => !manifestByKey.ContainsKey(key)).OrderBy(k => k.ToString()).ToList();
        var manifestOnly = manifestByKey.Keys.Where(key => !modelByKey.ContainsKey(key)).OrderBy(k => k.ToString()).ToList();

        if (modelOnly.Count > 0 || manifestOnly.Count > 0)
        {
            var message = new StringBuilder(
                "The (Class, Field) join failed — MasterLCModel.xml and the manifest disagree on the key set (docs/plan-motif.md, MOT-2).");
            if (modelOnly.Count > 0)
                message.Append($" In the model but not the manifest: {string.Join(", ", modelOnly)}.");
            if (manifestOnly.Count > 0)
                message.Append($" In the manifest but not the model: {string.Join(", ", manifestOnly)}.");
            throw new GeneratorException(message.ToString());
        }

        return modelByKey
            .Select(pair => new JoinedRow(pair.Value.DeclaringClass, pair.Value.FieldName, pair.Value.Kind, pair.Value.Sig, pair.Value.Card, manifestByKey[pair.Key]))
            .ToList();
    }
}
