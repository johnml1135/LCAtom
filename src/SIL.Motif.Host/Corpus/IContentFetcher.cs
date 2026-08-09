using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SIL.Motif.Host.Corpus;

/// <summary>
/// Turns a <see cref="DocumentSource"/> into bytes. The one place that touches the filesystem or the network
/// on the way in.
/// </summary>
/// <remarks>
/// An interface rather than a static helper for two reasons that both bite in practice: ingestion tests must
/// not depend on a host being reachable, and the fetching policy — timeouts, proxies, redirects, and the TLS
/// interception that corporate networks perform — is the kind of thing that gets special-cased once and then
/// spreads. Keeping it behind one seam means the special case has somewhere to live.
/// </remarks>
public interface IContentFetcher
{
    /// <summary>Retrieve the content of <paramref name="source"/>.</summary>
    /// <exception cref="IOException">The content could not be retrieved.</exception>
    Task<byte[]> FetchAsync(DocumentSource source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads files from disk and retrieves URLs over HTTP. The default for the CLI.
/// </summary>
/// <remarks>
/// <b>No retries, no caching, no redirect limit beyond the handler's default.</b> Deliberately plain: the
/// expected pipeline has an external tool do the difficult fetching and hand Motif files, so this exists for
/// the one-off case rather than for pulling a corpus at scale. If ingestion ever needs resumable downloads,
/// that is a sign the work has moved to the wrong side of the seam.
/// </remarks>
public sealed class ContentFetcher : IContentFetcher, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ContentFetcher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = http is null;
    }

    public async Task<byte[]> FetchAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        switch (source)
        {
            case DocumentSource.File file:
            {
                var path = Path.GetFullPath(file.Path);
                if (!System.IO.File.Exists(path))
                    throw new FileNotFoundException($"No file to ingest at '{path}'.", path);
                return await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }

            case DocumentSource.Url url:
            {
                try
                {
                    using var response = await _http
                        .GetAsync(url.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // The caller is ingesting a corpus, not debugging a socket. Say which URL failed.
                    throw new IOException($"Could not retrieve '{url.Uri}': {ex.Message}", ex);
                }
            }

            default:
                throw new NotSupportedException($"Unknown document source: {source.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
