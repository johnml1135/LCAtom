using System.Security.Cryptography;
using System.Text;

namespace SIL.Motif.Generator.Descriptions.Harvest;

/// <summary>
/// SHA-256 over a cited source fragment or a source file, rendered the way the rest of this repository
/// renders a digest: <c>sha256:</c> followed by 64 lowercase hex characters
/// (<c>SIL.Motif.Contract.Canonicalization.IntentDigest</c>). A local copy of four lines rather than a
/// project reference, because the generator deliberately references nothing but the model package.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two scales, two questions.</b> A <em>file</em> digest answers "is there anything to review at all?" —
/// it is what makes a moved checkout distinguishable from a changed source, and a commit hash cannot answer
/// that, because it moves for every unrelated commit in the repository. A <em>fragment</em> digest answers
/// "which rows?" — <c>MasterLCModel.xml</c> alone backs 66 descriptions, so a file-level signal there would
/// flag all 66 for a change to any one of them, and a check that cries wolf 66 times is a check nobody
/// reads.
/// </para>
/// <para>
/// <b>What a fragment digest is over.</b> The exact upstream text a description was copied from, and
/// nothing else — not the line number, not the surrounding element. Line numbers move whenever anything
/// above them is edited; the text is the thing whose change actually matters. The fragment's <em>identity</em>
/// is structural instead — <c>(file, class id, field id)</c>, which is how every harvester here already
/// addresses it — so the same fragment can always be re-found and re-hashed even after it has moved down
/// the file.
/// </para>
/// </remarks>
public static class SourceDigest
{
    /// <summary>Digest of one cited sentence, over its UTF-8 bytes.</summary>
    public static string OfText(string text) => Render(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Digest of a whole source artifact, over its bytes exactly as they sit on disk.</summary>
    public static string OfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Render(SHA256.HashData(stream));
        }
        catch (IOException ex)
        {
            throw new GeneratorException($"Could not hash source file '{path}': {ex.Message}", ex);
        }
    }

    private static string Render(byte[] hash) => "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
}
