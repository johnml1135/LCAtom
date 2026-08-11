using Org.Webpki.JsonCanonicalizer;

namespace SIL.Motif.Contract.Canonicalization;

/// <summary>
/// Thin wrapper around the RFC 8785 JSON Canonicalization Scheme (JCS) reference implementation
/// (Anders Rundgren / Samuel Erdtman's <c>org.webpki</c> canonicalizer, ported to C#; distributed
/// as the <c>jsoncanonicalizer</c> NuGet package). We deliberately do not hand-roll JCS's
/// member-name sort order or ES6 number serialization — a Python or Rust reimplementation of either
/// diverges from .NET's own output (ADR 0007).
/// </summary>
public static class CanonicalJson
{
    /// <summary>Canonicalizes arbitrary JSON text and returns the canonical form as UTF-8 bytes.</summary>
    public static byte[] CanonicalizeToUtf8(string json) => new JsonCanonicalizer(json).GetEncodedUTF8();

    /// <summary>Canonicalizes arbitrary JSON text and returns the canonical form as a string.</summary>
    public static string Canonicalize(string json) => new JsonCanonicalizer(json).GetEncodedString();
}
