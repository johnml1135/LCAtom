using System.Text.Json;

namespace SIL.Motif.Contract.Parsing;

/// <summary>
/// Forbids floating-point-shaped JSON number literals in semantic value content. See
/// docs/adr/0007-cross-language-digest-determinism.md decision 4: LibLCM's model has no
/// floating-point fields, so a float appearing in an operation's desired-value payload can only be
/// an unsupported custom-field type; forbidding it at parse time closes the open
/// floating-point-determinism question rather than deferring it to a lossy formatter choice.
/// </summary>
/// <remarks>
/// Does not apply to <c>confidence</c>, which is explicitly excluded from the intent digest and so
/// may safely be a float, nor to <c>extensions</c>, which is non-semantic tool data.
/// </remarks>
public static class JsonNumberPolicy
{
    public static void RejectFloats(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
                {
                    throw new ContractParseException(
                        $"Floating-point value at '{path}' ({raw}) is forbidden; only integral " +
                        "numbers are permitted in semantic operation content. See ADR 0007.");
                }
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    RejectFloats(property.Value, $"{path}.{property.Name}");
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    RejectFloats(item, $"{path}[{index}]");
                    index++;
                }
                break;
        }
    }
}
