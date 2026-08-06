using SIL.Motif.Host.LcmUtils;
using SIL.Motif.Tests.TestFixtures;
using SIL.LCModel;
using SIL.LCModel.Core.Text;
using SIL.LCModel.Infrastructure;
using Xunit;

namespace SIL.Motif.Tests.Host;

/// <summary>
/// <see cref="FwDataProjectLoader.Save"/> must not return until the <c>.fwdata</c> file on disk reflects
/// what was committed.
/// </summary>
/// <remarks>
/// <para>
/// This is not a theoretical property. <c>XMLBackendProvider.PerformCommit</c> enqueues the write on a
/// background thread and returns, so a <c>Save</c> that only calls <c>IActionHandler.Commit()</c> — which
/// is what this loader did until 2026-08-06, copied from <c>FwDataMiniLcmBridge</c> — returns before the
/// bytes exist. Nothing noticed while Motif only ever read the project through the cache. It broke the
/// moment the Dry Run started working from a <i>file copy</i>: the copy was one operation stale, its
/// baseline disagreed with the live cache, and Apply reported <b>footprint drift on a project nobody had
/// touched</b>. Twelve round-trip tests failed that way, and the message pointed at drift detection
/// rather than at the save.
/// </para>
/// <para>
/// So the assertion is deliberately end-to-end and file-level: mutate, save, then open the file
/// <i>separately</i> and demand the change be there. A cache-level assertion would pass even with the
/// bug, since the live cache always shows its own committed state.
/// </para>
/// </remarks>
[Collection(TestFixtures.LcmCacheTestCollection.Name)]
public sealed class SaveIsSynchronousTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _fwDataPath;
    private readonly FwDataProjectLoader _loader = new();
    private readonly LcmCache _cache;

    public SaveIsSynchronousTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "SIL.Motif.Tests.Save", Guid.NewGuid().ToString("N"));
        _fwDataPath = TestLangProjFixture.CopyToTempAndGetFwDataPath(_tempRoot);
        _cache = _loader.LoadCache(_fwDataPath);
    }

    [Fact]
    public void AfterSaveReturns_AFreshCopyOfTheFileAlreadyContainsTheChange()
    {
        var entry = _cache.ServiceLocator.GetInstance<ILexEntryRepository>().AllInstances().First();
        var entryGuid = entry.Guid;
        var wsHandle = _cache.DefaultAnalWs;
        const string marker = "zzSaveIsSynchronous";

        NonUndoableUnitOfWorkHelper.Do(_cache.ActionHandlerAccessor, () =>
            entry.Comment.set_String(wsHandle, TsStringUtils.MakeString(marker, wsHandle)));

        _loader.Save(_cache);

        // Copy and open with no further ceremony — exactly what a Dry Run does, and the step that
        // exposed the asynchronous save. No sleeping, no retrying: if the barrier is missing this must
        // fail, because a test that waits would hide the very defect it exists to catch.
        var copyRoot = Path.Combine(_tempRoot, "copy-immediately-after-save");
        using var copy = new ScratchCacheFactory(_loader).CreateFromFileCopy(_fwDataPath, copyRoot);

        var copiedEntry = copy.ServiceLocator.GetInstance<ILexEntryRepository>().GetObject(entryGuid);
        var copiedWsHandle = copy.WritingSystemFactory.GetWsFromStr(
            _cache.WritingSystemFactory.GetStrFromWs(wsHandle));

        Assert.Equal(marker, copiedEntry.Comment.get_String(copiedWsHandle).Text);
    }

    public void Dispose()
    {
        if (!_cache.IsDisposed) _cache.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }
}
