using System.ComponentModel;
using SIL.LCModel.Utils;

namespace SIL.LCAtom.Runner.Apply;

/// <summary>
/// A no-op <see cref="IProgress"/>, for calling public LibLCM APIs that require a progress
/// reporter (e.g. <c>ILexEntryRepository.ResetHomographs</c>) when nothing observes it — the
/// non-interactive runner equivalent of <c>SIL.LCAtom.Host.LcmUtils.LcmThreadedProgress</c>, kept
/// here so <c>SIL.LCAtom.Runner</c> does not need a reference to the Host project for this one
/// failure-path call.
/// </summary>
internal sealed class NullProgress : IProgress
{
    public static readonly NullProgress Instance = new();

    private NullProgress()
    {
    }

#pragma warning disable CS0067 // part of the interface; never raised by a no-op progress reporter
    public event CancelEventHandler? Canceling;
#pragma warning restore CS0067

    public void Step(int amount)
    {
    }

    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Position { get; set; }
    public int StepSize { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; }
    public ISynchronizeInvoke SynchronizeInvoke { get; } = new SingleThreadedSynchronizeInvoke();
    public bool IsIndeterminate { get; set; }
    public bool AllowCancel { get; set; }
}
