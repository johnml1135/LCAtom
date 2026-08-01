using System;
using SIL.LCModel;

namespace SIL.Motif.Runner.Caching;

/// <summary>
/// Thrown by <see cref="CacheReusability.EnsureReusable"/> when a caller attempts to Run or Apply
/// against an <see cref="LcmCache"/> instance already marked non-reusable by
/// <see cref="CacheReusability.MarkPoisoned"/>. Refusing outright — rather than silently proceeding
/// and returning a possibly-different digest — is the fix for the failure mode
/// docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 3 describes: a caller must
/// discard and reload the project (a fresh <see cref="LcmCache"/> instance) before computing a dry run or
/// applying again.
/// </summary>
public sealed class CachePoisonedException : InvalidOperationException
{
    public CachePoisonedException(LcmCache cache, string reason)
        : base(
            "This LcmCache instance is no longer safe to reuse for Run/Apply: " + reason +
            " Reload the project (a fresh LcmCache instance) before trying again.")
    {
        Cache = cache;
        Reason = reason;
    }

    /// <summary>The poisoned cache instance, for a caller that wants to discard/dispose it.</summary>
    public LcmCache Cache { get; }

    /// <summary>The reason recorded when the cache was marked poisoned.</summary>
    public string Reason { get; }
}
