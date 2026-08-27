// Adapted from languageforge-lexbox's FwDataMiniLcmBridge/LcmUtils/LcmUi.cs (SIL Global, MIT License).

using System.ComponentModel;
using SIL.LCModel;

namespace SIL.Motif.Host.LcmUtils;

/// <summary>
/// Non-interactive <see cref="ILcmUI"/> for a headless host. There is no user to prompt, so
/// destructive/ambiguous decisions fail loudly (<see cref="NotImplementedException"/>) rather than
/// silently guessing, while informational callbacks just write to the console.
/// </summary>
public class HeadlessLcmUi(ISynchronizeInvoke synchronizeInvoke) : ILcmUI
{
    public void DisplayCircularRefBreakerReport(string msg, string caption)
    {
        Console.WriteLine(msg);
    }

    /// <summary>Refuses a conflicted save, because neither answer is safe without a person.</summary>
    /// <remarks>
    /// LibLCM asks this when a foreign peer committed between this process's read and its save. Answering
    /// <c>true</c> reverts to the saved state and lets <c>Save</c> return normally, so the caller is told
    /// its work landed when it was discarded; answering <c>false</c> keeps changes LibLCM has already
    /// judged unreconcilable. A headless process has neither a person to ask nor standing to pick, so it
    /// fails where the caller can see it. Pinned by
    /// `AConflictingSaveFailsLoudlyRatherThanSilentlyDiscardingTheWork`.
    /// </remarks>
    public bool ConflictingSave() =>
        throw new NotSupportedException(
            "A conflicting save needs a decision this headless host cannot make: another writer changed " +
            "the project since it was read.");

    public bool ConnectionLost()
    {
        throw new NotImplementedException();
    }

    public FileSelection ChooseFilesToUse()
    {
        throw new NotImplementedException();
    }

    public bool RestoreLinkedFilesInProjectFolder()
    {
        throw new NotImplementedException();
    }

    public YesNoCancel CannotRestoreLinkedFilesToOriginalLocation()
    {
        throw new NotImplementedException();
    }

    public void DisplayMessage(MessageType type, string message, string caption, string helpTopic)
    {
        Console.WriteLine("{0}: {1}", type, message);
    }

    public void ReportException(Exception error, bool isLethal)
    {
        Console.WriteLine("Got exception: {0}: {1}\n{2}", error.GetType(), error.Message, error);
    }

    public void ReportDuplicateGuids(string errorText)
    {
        Console.WriteLine("Duplicate GUIDs: " + errorText);
    }

    public bool Retry(string msg, string caption)
    {
        Console.WriteLine(msg);
        return true;
    }

    public bool OfferToRestore(string projectPath, string backupPath)
    {
        return false;
    }

    public void Exit()
    {
        Console.WriteLine("Exiting");
    }

    public ISynchronizeInvoke SynchronizeInvoke => synchronizeInvoke;

    public DateTime LastActivityTime => DateTime.Now;
}
