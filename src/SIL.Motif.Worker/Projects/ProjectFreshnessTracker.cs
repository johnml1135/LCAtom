using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Worker.Projects;

/// <summary>
/// Classifies a Baseline from the current live-host epoch when one is observed; otherwise from a
/// caller-supplied saved-project probe, when the caller provides one, and never by probing the saved
/// project itself.
/// </summary>
public enum BaselineFreshness
{
    Current,
    KnownOld,
    CurrentnessNotChecked
}

/// <summary>Maintains monotonically ordered freshness evidence for one project workspace.</summary>
public sealed class ProjectFreshnessTracker
{
    private readonly object _gate = new();
    private LiveProjectObservation? _current;

    public LiveProjectObservation? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool Register(LiveProjectObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            _current = observation;
            return true;
        }
    }

    public bool Update(LiveProjectObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (_current is null ||
                !StringComparer.Ordinal.Equals(_current.HostSessionId, observation.HostSessionId) ||
                observation.EditGeneration < _current.EditGeneration) return false;
            _current = observation;
            return true;
        }
    }

    public bool Disconnect(string hostSessionId)
    {
        if (string.IsNullOrWhiteSpace(hostSessionId)) return false;
        lock (_gate)
        {
            if (_current is null ||
                !StringComparer.Ordinal.Equals(_current.HostSessionId, hostSessionId)) return false;
            _current = null;
            return true;
        }
    }

    /// <summary>
    /// Classifies <paramref name="token"/> against the current live observation. When no live host is
    /// observed and <paramref name="savedProjectProbe"/> is supplied, it is invoked to obtain the saved
    /// project's semantic digest without taking the project lock or disturbing a live host; a live
    /// observation, being the better evidence, is always preferred over the probe when both are available.
    /// A null probe, or a probe that returns null (unavailable), leaves the baseline's currentness unchecked.
    /// </summary>
    public BaselineFreshness Check(BaselineToken token, Func<string?>? savedProjectProbe = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        LiveProjectObservation? observation;
        lock (_gate) { observation = _current; }

        if (observation is not null)
        {
            if (observation.HasUnsavedChanges) return BaselineFreshness.KnownOld;
            if (StringComparer.Ordinal.Equals(observation.HostSessionId, token.CapturedHostSessionId))
                return token.CapturedEditGeneration == observation.EditGeneration
                    ? BaselineFreshness.Current
                    : BaselineFreshness.KnownOld;
            return StringComparer.Ordinal.Equals(observation.SavedSemanticDigest, token.SemanticSnapshotDigest)
                ? BaselineFreshness.Current
                : BaselineFreshness.KnownOld;
        }

        var probedDigest = savedProjectProbe?.Invoke();
        if (probedDigest is null) return BaselineFreshness.CurrentnessNotChecked;
        return StringComparer.Ordinal.Equals(probedDigest, token.SemanticSnapshotDigest)
            ? BaselineFreshness.Current
            : BaselineFreshness.KnownOld;
    }
}
