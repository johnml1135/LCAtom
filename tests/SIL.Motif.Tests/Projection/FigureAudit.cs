using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SIL.Motif.Tests.Projection;

/// <summary>
/// Extracts every "figure" a rendered report's text carries — a double-quoted value, or a bare
/// id/digest-shaped token — and asserts each is one of the JSON's own leaf values, emitted from the
/// same projection. This is the acceptance test for ADR 0021 decision 2, made mechanical: a figure the
/// text states that the JSON does not is exactly what "the projection layer was skipped" would produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership is exact, against a set.</b> An earlier version joined every leaf into one string and
/// used a substring check, which passed a text figure of <c>12</c> against a JSON value of <c>512</c> —
/// a false pass built into the comparison rather than missing from its coverage. Nothing may reintroduce
/// that: the whole point is to catch a number the text states and the JSON does not.
/// </para>
/// <para>
/// Booleans are collected. The same earlier version walked only strings and numbers, so text rendering
/// a flag as prose passed against JSON carrying the opposite value, because neither <c>true</c> nor
/// <c>false</c> was ever in the set to disagree with.
/// </para>
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
/// What it still cannot see is unquoted prose: a renderer writing <c>status: proposed</c> states a
/// figure this sweep does not recognise as one. Rendering report values unquoted is therefore outside
/// what this audit defends, and the per-projection contract tests are what cover the shape itself.
/// </para>
/// </remarks>
internal static class FigureAudit
{
    private static readonly Regex QuotedValue = new("\"([^\"]+)\"", RegexOptions.Compiled);

    // The algorithm prefix is part of the value: sha256:abc... is one figure, not a stray token after a colon.
    private static readonly Regex IdOrDigestToken =
        new(@"\b(?:[A-Za-z0-9]+:)?[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled);

    public static void AssertEveryTextFigureAppearsInJson(string text, string json)
    {
        var leafValues = CollectLeafValues(json);
        var checkedAny = false;

        foreach (Match match in QuotedValue.Matches(text))
        {
            var value = match.Groups[1].Value;
            if (value.Length == 0)
                continue;
            AssertIsALeaf(value, leafValues, "quoted value");
            checkedAny = true;
        }

        foreach (Match match in IdOrDigestToken.Matches(text))
        {
            if (!HasDigit(match.Value))
                continue;
            AssertIsALeaf(match.Value, leafValues, "id- or digest-shaped token");
            checkedAny = true;
        }

        Assert.True(checkedAny, "Expected at least one figure-shaped token in the rendered text to audit.");
    }

    private static void AssertIsALeaf(string value, IReadOnlySet<string> leafValues, string what)
    {
        if (leafValues.Contains(value)) return;

        Assert.Fail(
            $"The rendered text states a {what} the JSON does not carry, so a reader would have to " +
            $"parse prose to recover it.{Environment.NewLine}" +
            $"  in text: {value}{Environment.NewLine}" +
            $"  JSON leaf values: {string.Join(", ", leafValues.OrderBy(v => v, StringComparer.Ordinal))}");
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

    private static IReadOnlySet<string> CollectLeafValues(string json)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        Collect(document.RootElement, values);
        return values;
    }

    private static void Collect(JsonElement element, HashSet<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, values);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, values);
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                values.Add(text);
                CollectEmbeddedJson(text, values);
                CollectPathSegments(text, values);
                break;
            case JsonValueKind.Number:
                values.Add(element.GetRawText());
                break;
            case JsonValueKind.True:
                values.Add("true");
                break;
            case JsonValueKind.False:
                values.Add("false");
                break;
        }
    }

    // A path's own segments are recoverable from the path, so a folder name the text shows is carried.
    private static void CollectPathSegments(string text, HashSet<string> values)
    {
        if (text.IndexOf('/') < 0 && text.IndexOf('\\') < 0) return;

        foreach (var segment in text.Split('/', '\\'))
        {
            if (segment.Length > 0)
                values.Add(segment);
        }
    }

    // An operation payload is embedded JSON on both sides; the text shows its keys and values, so they count.
    private static void CollectEmbeddedJson(string text, HashSet<string> values)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return;

        try
        {
            using var embedded = JsonDocument.Parse(text);
            CollectWithNames(embedded.RootElement, values);
        }
        catch (JsonException)
        {
            // Not JSON after all: the string leaf itself is already in the set, which is all this owed.
        }
    }

    private static void CollectWithNames(JsonElement element, HashSet<string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                values.Add(property.Name);
                CollectWithNames(property.Value, values);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectWithNames(item, values);

            return;
        }

        Collect(element, values);
    }
}
