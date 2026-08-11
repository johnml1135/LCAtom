using SIL.Motif.Generator.Derivation;
using SIL.Motif.Generator.Join;

namespace SIL.Motif.Generator.Emit;

/// <summary>
/// Assembles an <see cref="IntegerEnumFieldSpec"/> from one <see cref="JoinedRow"/>, calling the same
/// <see cref="GroupDerivation"/>/<see cref="ConstructDerivation"/>/<see cref="KindNameDerivation"/>
/// <see cref="BasicFieldSpecBuilder"/> calls for the other shapes — glue, not a second place that
/// decides group/construct/kind — plus parsing the manifest's <c>EnumValues</c> column, which no other
/// builder needs yet.
/// </summary>
public static class IntegerEnumFieldSpecBuilder
{
    public static IntegerEnumFieldSpec Build(JoinedRow row)
    {
        var group = GroupDerivation.Derive(row.DeclaringClass);
        var construct = ConstructDerivation.Derive(row.DeclaringClass);
        var members = ParseEnumValues(row);

        return new IntegerEnumFieldSpec(
            DeclaringClass: row.DeclaringClass,
            FieldName: row.FieldName,
            Sig: row.Sig,
            Group: group,
            Construct: construct,
            SetKind: KindNameDerivation.DeriveOne(group, construct, "set", row.FieldName),
            ClearKind: KindNameDerivation.DeriveOne(group, construct, "clear", row.FieldName),
            TargetInterface: "I" + row.DeclaringClass,
            SnapshotFieldConstant: row.DeclaringClass + row.FieldName,
            EnumMembers: members,
            ZeroMemberName: ZeroMemberNameOf(row, members));
    }

    public static IReadOnlyList<IntegerEnumFieldSpec> BuildAll(IReadOnlyList<JoinedRow> rows) =>
        rows.Select(Build).ToList();

    /// <summary>The name of the <c>0</c> member the generated <c>clear</c> writes; throws if the enum has none, since that shape needs a decision about what clearing it means, not a template.</summary>
    private static string ZeroMemberNameOf(JoinedRow row, IReadOnlyList<(int Value, string Name)> members)
    {
        foreach (var member in members)
        {
            if (member.Value == 0) return member.Name;
        }

        throw new GeneratorException(
            $"{row.Key}: a basic-Integer-enum field needs a '0=' member, because the derived 'clear' verb " +
            $"writes 0 and this shape range-checks against the manifest's own EnumValues " +
            $"('{row.Manifest.EnumValues}'), which does not include it.");
    }

    /// <summary>Parses <c>"0=Undecided;1=Correct;2=Incorrect"</c> into ordered <c>(value, name)</c> pairs; fails closed on a missing or malformed <c>EnumValues</c> mapping rather than emit a template with no range check.</summary>
    private static IReadOnlyList<(int Value, string Name)> ParseEnumValues(JoinedRow row)
    {
        var raw = row.Manifest.EnumValues;
        if (string.IsNullOrWhiteSpace(raw) || raw == "unknown")
        {
            throw new GeneratorException(
                $"{row.Key}: a basic-Integer-enum field needs a confirmed manifest 'EnumValues' " +
                $"mapping ('value=Name;...'), found '{raw}'.");
        }

        var members = new List<(int Value, string Name)>();
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var value))
            {
                throw new GeneratorException(
                    $"{row.Key}: could not parse 'EnumValues' entry '{pair}' (expected 'value=Name').");
            }

            members.Add((value, parts[1]));
        }

        return members.OrderBy(m => m.Value).ToList();
    }
}
