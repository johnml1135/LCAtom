using System;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Where the bytes of a Document come from: a file already on disk, or a location to retrieve them from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both are supported and the file case is the expected one.</b> The normal pipeline has an external tool —
/// linguistic-assistant, in Python — do the fetching, the cleaning and the licence lookup, then hand Motif
/// finished files. That tool already knows about eBible's verse-per-line layout, OPUS's alignment XML and the
/// TLS interception on corporate networks; none of that belongs in Motif.
/// </para>
/// <para>
/// The URL case exists because a one-off ingestion should not require a detour through another program, and
/// because <b>recording where something came from is worthless if nothing ever checks that the location
/// resolves</b>. It retrieves through <see cref="IContentFetcher"/> rather than reaching for the network
/// directly, so tests never depend on a host being up and so the fetching policy stays in one replaceable
/// place.
/// </para>
/// </remarks>
public abstract record DocumentSource
{
    private DocumentSource() { }

    /// <summary>Bytes already on this machine, put there by whatever produced them.</summary>
    /// <param name="Path">An absolute or working-directory-relative path to the file.</param>
    public sealed record File(string Path) : DocumentSource
    {
        public override string Describe() => Path;
    }

    /// <summary>A location the content is retrieved from at ingestion time.</summary>
    /// <param name="Uri">The absolute URI to retrieve.</param>
    public sealed record Url(Uri Uri) : DocumentSource
    {
        public override string Describe() => Uri.ToString();
    }

    /// <summary>How this source reads in a message to a person.</summary>
    public abstract string Describe();

    /// <summary>Convenience for callers holding a string that may be either.</summary>
    /// <remarks>
    /// A string that parses as an absolute <c>http</c> or <c>https</c> URI is a URL; everything else is a path.
    /// Deliberately narrow: <c>file:</c> URIs, <c>ftp:</c> and anything more exotic are not silently accepted as
    /// something to go and fetch.
    /// </remarks>
    public static DocumentSource Parse(string fileOrUrl)
    {
        if (string.IsNullOrWhiteSpace(fileOrUrl))
            throw new ArgumentException("A document source must be a file path or a URL.", nameof(fileOrUrl));

        if (Uri.TryCreate(fileOrUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new Url(uri);
        }

        return new File(fileOrUrl);
    }
}
