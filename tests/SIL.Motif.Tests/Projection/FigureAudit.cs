using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// Extracts every "figure" a rendered report's text carries — a double-quoted value, or a bare
/// id/digest-shaped token — and asserts each also appears somewhere among the JSON's own leaf values,
/// emitted from the same projection. This is the acceptance test for ADR 0021 decision 2, made
/// mechanical: a figure the text states that the JSON does not is exactly what "the projection layer
/// was skipped" would produce.
/// </summary>
/// <remarks>
/// <para>
/// Double quotes only, not single: a renderer's fixed prose also uses single quotes to name a command
/// ('apply' will require it), which is not report data and would false-positive if swept.
/// </para>
/// <para>
/// A bare token must contain a digit to count as id/digest-shaped — a field label like
/// <c>currentIntentDigest</c> is long enough to match the length threshold on its own, but contains no
/// digit, while every real canonical id, hex digest, GUID and timestamp this codebase renders does.
/// </para>
/// <para>
/// Comparison walks the parsed JSON tree rather than the raw JSON text, so a value JSON had to
/// backslash-escape (a Windows path, for instance) still matches the same value read out of the text.
/// </para>
/// </remarks>
internal static class FigureAudit
{
    private static readonly Regex QuotedValue = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex IdOrDigestToken = new(@"\b[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled);

    public static void AssertEveryTextFigureAppearsInJson(string text, string json)
    {
        var leafValues = CollectLeafValues(json);
        var checkedAny = false;

        foreach (Match match in QuotedValue.Matches(text))
        {
            var value = match.Groups[1].Value;
            if (value.Length == 0)
                continue;
            Assert.Contains(value, leafValues, StringComparison.Ordinal);
            checkedAny = true;
        }

        foreach (Match match in IdOrDigestToken.Matches(text))
        {
            if (!HasDigit(match.Value))
                continue;
            Assert.Contains(match.Value, leafValues, StringComparison.Ordinal);
            checkedAny = true;
        }

        Assert.True(checkedAny, "Expected at least one figure-shaped token in the rendered text to audit.");
    }

    private static bool HasDigit(string value)
    {
        foreach (var c in value)
        {
            if (char.IsDigit(c))
                return true;
        }
        return false;
    }

    // Every string/number leaf in the JSON tree, joined so a substring check finds a prefixed value.
    private static string CollectLeafValues(string json)
    {
        var sb = new StringBuilder();
        using var document = JsonDocument.Parse(json);
        Collect(document.RootElement, sb);
        return sb.ToString();
    }

    private static void Collect(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, sb);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, sb);
                break;
            case JsonValueKind.String:
                sb.Append(element.GetString()).Append('\n');
                break;
            case JsonValueKind.Number:
                sb.Append(element.GetRawText()).Append('\n');
                break;
        }
    }
}
