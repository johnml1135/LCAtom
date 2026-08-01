using Xunit;

namespace SIL.Motif.Tests.TestFixtures;

/// <summary>
/// Groups every test class that bootstraps a real <c>LcmCache</c> (via
/// <see cref="SIL.Motif.Host.LcmUtils.FwDataProjectLoader"/>) into one xUnit collection so they
/// never run concurrently with each other.
/// </summary>
/// <remarks>
/// xUnit runs distinct test classes (each its own implicit collection) in parallel by default.
/// Concurrently bootstrapping two separate <c>LcmCache</c> instances in one process — even from two
/// unrelated temp-copied projects — was observed to corrupt LibLCM's own project-startup pass
/// (<c>ArgumentOutOfRangeException</c> inside <c>SIL.LCModel.DomainServices.CircularRefBreakerService
/// .CheckForCircularRef</c>, called from <c>BackendProvider.StartupExtantLanguageProject</c>): that
/// service is not safe to run from two threads at once in this LibLCM version. Serializing these
/// tests via a shared collection avoids the race without touching LibLCM itself.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LcmCacheTestCollection
{
    public const string Name = "LcmCache tests (serialized: LcmCache bootstrap is not concurrency-safe)";
}
