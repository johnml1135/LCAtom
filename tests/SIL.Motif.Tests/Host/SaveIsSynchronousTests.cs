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
/// background thread and returns, so a naive <c>Save</c> that only calls <c>IActionHandler.Commit()</c>
/// returns before the bytes exist on disk. That is invisible as long as Motif only reads the project
/// through the live cache; it surfaces the moment a Dry Run opens a separate <i>file copy</i>, because a
/// stale copy's baseline disagrees with the cache and Apply misreports the disagreement as
/// <b>footprint drift on a project nobody touched</b>.
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

        // No sleeping or retrying: copy immediately, exactly what a Dry Run does — waiting would hide the defect.
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
