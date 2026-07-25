using System;

namespace SIL.LCAtom.Model.AppliedLog;

/// <summary>
/// One parsed applied-change-log record: the combination of a <c>CmResource.Version</c> (the
/// stable <c>changeSetId</c> GUID, used for identity matching) and its <c>CmResource.Name</c>,
/// split into fields. See docs/applied-log.md, "Record format".
/// </summary>
/// <param name="ChangeSetId">
/// The stable <c>changeSetId</c> GUID this entry records. Stored in <c>CmResource.Version</c>,
/// never inside <c>Name</c>. This is the field "already applied?" matches on.
/// </param>
/// <param name="Format">The decimal format version (currently always <see cref="AppliedLogFormat.CurrentFormatVersion"/>).</param>
/// <param name="TimestampUtc">
/// UTC ISO 8601 basic, fixed 16 characters, e.g. <c>20260724T055701Z</c>.
/// </param>
/// <param name="User">
/// The opaque applier identity recorded at apply time. At most 64 characters, may be empty, never
/// contains <c>|</c> or a control character.
/// </param>
/// <param name="IntentDigest">
/// The Change Set's intent digest recorded at apply, as fixed-length lowercase hex (the
/// <c>sha256:</c> prefix is not stored — see <see cref="AppliedLogFormat"/>).
/// </param>
/// <param name="Description">
/// Free single-line human-facing text, at most 128 characters. May contain <c>|</c>; never a
/// control character.
/// </param>
public sealed record AppliedLogEntry(
    Guid ChangeSetId,
    int Format,
    string TimestampUtc,
    string User,
    string IntentDigest,
    string Description);
