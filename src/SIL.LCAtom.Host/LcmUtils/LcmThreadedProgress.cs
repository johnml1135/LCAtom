// Adapted from FwDataMiniLcmBridge, Copyright (c) SIL Global, licensed under the MIT License.
// Source: languageforge-lexbox/backend/FwLite/FwDataMiniLcmBridge/LcmUtils/LcmThreadedProgress.cs
// https://github.com/sillsdev/languageforge-lexbox

using System.ComponentModel;
using SIL.LCModel.Utils;

namespace SIL.LCAtom.Host.LcmUtils;

/// <summary>
/// Non-interactive <see cref="IThreadedProgress"/> shim. LibLCM's project-load path requires a
/// progress reporter and a <see cref="ISynchronizeInvoke"/> marshaller even when nothing observes
/// progress; this runs the supplied task synchronously on the calling thread.
/// </summary>
public class LcmThreadedProgress : IThreadedProgress
{
    private readonly SingleThreadedSynchronizeInvoke _synchronizeInvoke = new();

#pragma warning disable CS0067 // part of the interface; headless host never cancels
    public event CancelEventHandler? Canceling;
#pragma warning restore CS0067

    public void Step(int amount)
    {
    }

    public string? Title { get; set; }
    public string? Message { get; set; }
    public int Position { get; set; }
    public int StepSize { get; set; }
    public int Minimum { get; set; }
    public int Maximum { get; set; }

    public ISynchronizeInvoke SynchronizeInvoke => _synchronizeInvoke;

    public bool IsIndeterminate { get; set; }
    public bool AllowCancel { get; set; }

    public object RunTask(Func<IThreadedProgress, object[], object> backgroundTask, params object[] parameters)
    {
        return backgroundTask(this, parameters);
    }

    public object RunTask(bool fDisplayUi,
        Func<IThreadedProgress, object[], object> backgroundTask,
        params object[] parameters)
    {
        return backgroundTask(this, parameters);
    }

    public bool Canceled => false;

    public bool IsCanceling => false;
}
