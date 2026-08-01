# ADR 0007 — Cross-language digest determinism

Status: accepted (2026-07-24)

## Context

Motif promises C#/Python/Rust consumers agree byte-for-byte on canonical JSON (RFC 8785) and the
resulting digests. A stress test found the concrete points where a Python or Rust reimplementation
diverges from .NET. This resolves the previously-open "floating-point policy" item. Evidence:
[stress-test findings](../stress-test-findings.md).

## Decisions

1. **Normalization is a versioned, shipped artifact.** NFSC and NFC resolve to SIL's *custom* ICU
   data (`nfc_fw.nrm`/`nfkc_fw.nrm`), gated at runtime by `HaveCustomIcuLibrary`
   (`CustomIcu.cs:398-437`) — so even two .NET machines diverge without it, and Python/Rust stdlib
   normalizers cannot reproduce it at all. Motif ships that normalization data as a versioned
   contract artifact, folds its version into `projectionVersion`, requires every implementation to
   bind to that data (a shared native ICU binding, not stdlib/crate Unicode tables), and asserts its
   presence as a runner precondition rather than trusting the environment.

2. **Unordered collections sort by byte-ordinal comparison of the UTF-8 encoding of the canonical-id
   string** (prefix included). Decode-then-compare-as-GUID is forbidden — it already disagrees between
   .NET (`Guid.CompareTo` mixed-endian) and Python/Rust (big-endian), and base64url is not
   order-preserving.

3. **Object member names sort per RFC 8785** (UTF-16 code-unit order); implementations use a
   JCS-conformant serializer, never a native `sorted()`.

4. **Float/Numeric custom fields are forbidden at the schema level.** LibLCM's model has no
   floating-point fields and `confidence` is excluded from the intent digest, but
   `CellarPropertyType.Float/Numeric` exist and `AddCustomField` accepts them unguarded. Forbidding
   them closes the open floating-point question. If ever reintroduced, a single JCS-conformant number
   formatter (ECMAScript `Number::toString`) is mandated on all three sides.

5. **GUID ↔ canonical-id uses network-order bytes** (already specified). The contract adds the
   mixed-endian negative example (`uuid.bytes_le` / .NET-interop helpers), because the failure mode is
   reaching for the wrong *named* helper, not a missing one.

6. **GenDate and Binary canonical encodings must be pinned before Phase 1 fixtures** (partial/BCE
   GenDate; Binary/TextPropBinary base64 alphabet and padding).

## Consequences

- The normalization data file becomes a shipped, versioned deliverable that all consumers bind to;
  its version participates in the semantic digest.
- The canonical-JSON section pins the unordered-collection comparator and forbids float custom fields.
- Conformance vectors must be generated and checked from all three language runtimes, not only C#.
