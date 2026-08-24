using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;

namespace SIL.Motif.Worker.Projects;

/// <summary>Classifies a Baseline only from the current live-host epoch or an explicit saved-project probe.</summary>
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

    public BaselineFreshness Check(BaselineToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_gate)
        {
            if (_current is null) return BaselineFreshness.CurrentnessNotChecked;
            if (_current.HasUnsavedChanges) return BaselineFreshness.KnownOld;
            if (StringComparer.Ordinal.Equals(_current.HostSessionId, token.CapturedHostSessionId))
                return token.CapturedEditGeneration == _current.EditGeneration
                    ? BaselineFreshness.Current
                    : BaselineFreshness.KnownOld;
            return StringComparer.Ordinal.Equals(_current.SavedSemanticDigest, token.SemanticSnapshotDigest)
                ? BaselineFreshness.Current
                : BaselineFreshness.KnownOld;
        }
    }
}
