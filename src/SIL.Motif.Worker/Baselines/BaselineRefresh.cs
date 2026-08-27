using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SIL.LCModel;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.LiveHost.Baselines;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker.Baselines;

/// <summary>
/// Captures a project's Baseline and publishes it, as one step.
/// </summary>
/// <remarks>
/// <para>
/// Four things have to happen in order and all four have to happen: write the bundle from a saved cache,
/// verify the bytes against the token the writer declared, publish the verified bundle atomically under
/// its digest, and record the publication so a Dry Run can find it. Performing three of them leaves a
/// project that reports a fresh Baseline and has none.
/// </para>
/// <para>
/// They live here rather than in the caller because the order and the completeness are the whole content
/// of the operation — a caller holding four collaborators has to know all four, and knowing three is
/// indistinguishable from success.
/// </para>
/// <para>
/// <b>Two different things are called a project identity, and confusing them publishes a bundle into the
/// wrong root.</b> A Baseline is filed under LibLCM's own project GUID, which is what
/// <c>BaselineBundleWriter</c> stamps into the token it returns.
/// <see cref="ProjectLocator.FieldWorksProjectIdentity"/> is a file-stem-shaped name for finding a project
/// on disk, and renaming or copying the file changes it. This module derives the destination identity from
/// the open model so a caller cannot supply the wrong one.
/// </para>
/// </remarks>
public sealed class BaselineRefresh
{
    private readonly BaselineRepository _baselines;
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>Creates a refresh that publishes into one runner-owned Baseline root.</summary>
    internal BaselineRefresh(BaselineRepository baselines, string root, Func<DateTimeOffset>? now = null)
    {
        _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A Baseline root is required.", nameof(root));
        _root = root;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Captures the open project and publishes the result, returning what was published.</summary>
    /// <remarks>
    /// The cache must already be saved: the writer reads the project's files, so unsaved edits would be
    /// captured as the state before them.
    /// </remarks>
    public async Task<BaselineToken> RefreshAsync(LcmCache savedCache, ProjectLocator project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(savedCache);
        ArgumentNullException.ThrowIfNull(project);

        var staging = Path.Combine(_root, "captures");
        Directory.CreateDirectory(staging);
        var bundlePath = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var token = await WriteAsync(savedCache, bundlePath, cancellationToken).ConfigureAwait(false);
            var transfer = Verified(bundlePath);
            var publication = await new BaselineBundleReceiver().PublishVerifiedAsync(
                transfer, token,
                new BaselinePublicationTarget(Path.Combine(_root, "baselines"),
                    ProjectIdentityOf(savedCache)),
                cancellationToken).ConfigureAwait(false);
            _baselines.Record(ProjectWorkspaceKey.Compute(project), publication, _now());
            return publication.Token;
        }
        finally
        {
            // The staged bundle is consumed by publication; leaving it would grow without bound.
            try { File.Delete(bundlePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // LibLCM's project GUID, not the locator's file-stem identity: see this type's remarks.
    private static string ProjectIdentityOf(LcmCache savedCache) =>
        savedCache.LangProject.Guid.ToString("D");

    private static async Task<BaselineToken> WriteAsync(LcmCache savedCache, string bundlePath,
        CancellationToken cancellationToken)
    {
        using var destination = File.Create(bundlePath);
        return await new BaselineBundleWriter().WriteAsync(savedCache, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    /// Hashes what actually landed on disk rather than trusting what the writer reported writing.
    private static VerifiedBinaryTransfer Verified(string bundlePath)
    {
        using var stream = File.OpenRead(bundlePath);
        using var sha = SHA256.Create();
        var digest = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        return new VerifiedBinaryTransfer(Guid.NewGuid().ToString("N"), bundlePath, stream.Length, digest);
    }
}
