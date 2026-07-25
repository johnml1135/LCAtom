using System;
using System.Collections.Generic;
using System.Globalization;

namespace SIL.LCAtom.Model.AppliedLog;

/// <summary>
/// Packs and parses the single-line <c>CmResource.Name</c> provenance string LCAtom writes for
/// every applied-change-log entry. See docs/applied-log.md, "Record format".
/// </summary>
/// <remarks>
/// <c>Version = &lt;changeSetId&gt;</c><br/>
/// <c>Name = LCAtom|&lt;format&gt;|&lt;timestamp&gt;|&lt;user&gt;|&lt;intentDigest&gt;|&lt;description&gt;</c>
///
/// Every field before the description is fixed-width or constrained, so the description is a free
/// tail and needs no escaping: parsing splits on the first five <c>|</c> and takes the remainder.
/// This type never sees or produces the <c>changeSetId</c> itself (that lives in
/// <c>CmResource.Version</c>, a Guid, not text) — see <see cref="AppliedLogEntry.ChangeSetId"/>.
/// </remarks>
public static class AppliedLogFormat
{
    /// <summary>Literal prefix identifying entries this runner owns.</summary>
    public const string Prefix = "LCAtom";

    /// <summary>The current decimal format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Fixed length of the UTC ISO 8601 basic timestamp field, e.g. <c>20260724T055701Z</c>.</summary>
    public const int TimestampLength = 16;

    /// <summary>Maximum length of the <c>user</c> field.</summary>
    public const int MaxUserLength = 64;

    /// <summary>Maximum length of the <c>description</c> field.</summary>
    public const int MaxDescriptionLength = 128;

    /// <summary>Maximum total length of the packed <c>Name</c> string.</summary>
    public const int MaxNameLength = 256;

    /// <summary>Fixed length of the stored intent-digest hex (SHA-256, no <c>sha256:</c> prefix).</summary>
    public const int IntentDigestHexLength = 64;

    private const char FieldSeparator = '|';
    private static readonly string PrefixWithSeparator = Prefix + FieldSeparator;

    /// <summary>
    /// Renders <paramref name="entry"/>'s fields (everything except <see cref="AppliedLogEntry.ChangeSetId"/>,
    /// which is stored separately as <c>CmResource.Version</c>) as the packed <c>Name</c> string.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A field violates docs/applied-log.md's constraints — rejected here rather than truncated,
    /// per that document: "Overlong, multi-line, or control-character input is rejected at
    /// validation rather than truncated."
    /// </exception>
    public static string Format(AppliedLogEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        ValidateFormatVersion(entry.Format);
        ValidateTimestamp(entry.TimestampUtc);
        ValidateUser(entry.User);
        ValidateIntentDigestHex(entry.IntentDigest);
        ValidateDescription(entry.Description);

        var name = string.Join(
            FieldSeparator.ToString(CultureInfo.InvariantCulture),
            Prefix,
            entry.Format.ToString(CultureInfo.InvariantCulture),
            entry.TimestampUtc,
            entry.User,
            entry.IntentDigest,
            entry.Description);

        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Packed applied-log Name is {name.Length} characters; the limit is {MaxNameLength}.",
                nameof(entry));
        }

        return name;
    }

    /// <summary>
    /// Builds the fixed 16-character UTC ISO 8601 basic timestamp for <paramref name="utc"/>
    /// (which must already be UTC), e.g. <c>20260724T055701Z</c>.
    /// </summary>
    public static string FormatTimestamp(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("Expected a UTC DateTime, got Local.", nameof(utc));

        return utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Attempts to parse a <c>CmResource.Name</c> string into the non-identity fields of an
    /// <see cref="AppliedLogEntry"/>, combining it with the already-known <paramref name="changeSetId"/>
    /// (the resource's <c>Version</c>). Returns <c>false</c> both when <paramref name="name"/> is a
    /// foreign entry (no <c>LCAtom</c> prefix — never touched, per docs/applied-log.md, "Foreign
    /// entries") and when it carries the prefix but fails to parse (a diagnostic condition for the
    /// caller to surface, per the same section) — <paramref name="isForeign"/> distinguishes the two.
    /// </summary>
    public static bool TryParse(
        string? name, Guid changeSetId, out AppliedLogEntry? entry, out bool isForeign, out string? error)
    {
        entry = null;
        isForeign = false;

        if (string.IsNullOrEmpty(name))
        {
            isForeign = true;
            error = "Name is null or empty.";
            return false;
        }

        if (!name!.StartsWith(PrefixWithSeparator, StringComparison.Ordinal))
        {
            isForeign = true;
            error = $"Name does not start with the '{PrefixWithSeparator}' prefix.";
            return false;
        }

        var fields = SplitOnFirstNSeparators(name, 5);
        if (fields.Length != 6)
        {
            error = $"Expected 6 '|'-delimited fields (prefix, format, timestamp, user, intentDigest, " +
                     $"description); found {fields.Length}.";
            return false;
        }

        var formatText = fields[1];
        var timestamp = fields[2];
        var user = fields[3];
        var intentDigest = fields[4];
        var description = fields[5];

        if (!int.TryParse(formatText, NumberStyles.None, CultureInfo.InvariantCulture, out var format))
        {
            error = $"'{formatText}' is not a valid decimal format version.";
            return false;
        }

        if (timestamp.Length != TimestampLength)
        {
            error = $"Timestamp '{timestamp}' is {timestamp.Length} characters; expected {TimestampLength}.";
            return false;
        }

        entry = new AppliedLogEntry(changeSetId, format, timestamp, user, intentDigest, description);
        error = null;
        return true;
    }

    private static void ValidateFormatVersion(int format)
    {
        if (format < 1)
            throw new ArgumentException($"Format version must be >= 1; got {format}.", nameof(format));
    }

    private static void ValidateTimestamp(string timestampUtc)
    {
        if (timestampUtc is null) throw new ArgumentNullException(nameof(timestampUtc));

        if (timestampUtc.Length != TimestampLength)
        {
            throw new ArgumentException(
                $"Timestamp '{timestampUtc}' is {timestampUtc.Length} characters; expected exactly " +
                $"{TimestampLength} (UTC ISO 8601 basic, e.g. 20260724T055701Z).",
                nameof(timestampUtc));
        }

        if (ContainsControlCharacter(timestampUtc))
            throw new ArgumentException("Timestamp must not contain control characters.", nameof(timestampUtc));
    }

    private static void ValidateUser(string user)
    {
        if (user is null) throw new ArgumentNullException(nameof(user));

        if (user.Length > MaxUserLength)
        {
            throw new ArgumentException(
                $"User identity is {user.Length} characters; the limit is {MaxUserLength}.", nameof(user));
        }

        if (user.IndexOf(FieldSeparator) >= 0)
            throw new ArgumentException("User identity must not contain '|'.", nameof(user));

        if (ContainsControlCharacter(user))
            throw new ArgumentException("User identity must not contain control characters (including newlines).", nameof(user));
    }

    private static void ValidateDescription(string description)
    {
        if (description is null) throw new ArgumentNullException(nameof(description));

        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Description is {description.Length} characters; the limit is {MaxDescriptionLength}.",
                nameof(description));
        }

        // '|' is explicitly permitted in the description (docs/applied-log.md, "Record format"
        // table: "<description> | free single-line text ... may contain |"). Control characters,
        // including newlines, are still rejected: the whole Name must stay single-line.
        if (ContainsControlCharacter(description))
            throw new ArgumentException("Description must not contain control characters (including newlines).", nameof(description));
    }

    private static void ValidateIntentDigestHex(string intentDigest)
    {
        if (intentDigest is null) throw new ArgumentNullException(nameof(intentDigest));

        if (intentDigest.Length != IntentDigestHexLength)
        {
            throw new ArgumentException(
                $"Intent digest hex is {intentDigest.Length} characters; expected exactly " +
                $"{IntentDigestHexLength} (SHA-256, no 'sha256:' prefix).",
                nameof(intentDigest));
        }

        foreach (var c in intentDigest)
        {
            var isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerHex)
            {
                throw new ArgumentException(
                    $"Intent digest '{intentDigest}' must be lowercase hexadecimal.", nameof(intentDigest));
            }
        }
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Splits <paramref name="text"/> on its first <paramref name="separatorCount"/> occurrences of
    /// <see cref="FieldSeparator"/>, returning up to <paramref name="separatorCount"/> + 1 pieces
    /// (fewer if there are not enough separators — the caller treats that as a parse failure).
    /// </summary>
    private static string[] SplitOnFirstNSeparators(string text, int separatorCount)
    {
        var result = new List<string>(separatorCount + 1);
        var start = 0;

        for (var i = 0; i < separatorCount; i++)
        {
            var index = text.IndexOf(FieldSeparator, start);
            if (index < 0)
                return result.ToArray();

            result.Add(text.Substring(start, index - start));
            start = index + 1;
        }

        result.Add(text.Substring(start));
        return result.ToArray();
    }
}
