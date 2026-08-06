using System;
using SIL.LCModel;
using SIL.LCModel.Core.KernelInterfaces;
using SIL.LCModel.Core.Text;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// The one LibLCM write every MultiUnicode/MultiString <c>set|clear</c> kind performs — modelled on,
/// and shared by, the generated per-field <c>*Lowering</c> classes that replaced the hand-written
/// <c>SetGlossLowering</c> (MOT-4). MultiUnicode and MultiString accessors (<c>IMultiUnicode</c>,
/// <c>IMultiString</c>) both ultimately implement <see cref="ITsMultiString"/>
/// (<c>get_String</c>/<c>set_String</c>) and <see cref="IMultiAccessorBase"/>
/// (<c>AvailableWritingSystemIds</c>) — verified by reflection against the pinned <c>SIL.LCModel</c>
/// package, not assumed — so one generic helper lowers both sigs identically; only the accessor
/// expression a generated field's lowering class passes in differs (<c>sense.Gloss</c> vs
/// <c>entry.CitationForm</c> vs <c>form.Form</c>, etc).
/// </summary>
internal static class MultiAlternativesFieldLowering
{
    /// <summary>Sets one writing-system alternative to <paramref name="text"/> — the same
    /// <c>set_String(wsHandle, TsStringUtils.MakeString(...))</c> call
    /// <c>SetGlossLowering.Apply</c> made.</summary>
    public static void ApplySet<TAccessor>(
        LcmCache cache, TAccessor accessor, string writingSystemTag, string text, string kind)
        where TAccessor : IMultiAccessorBase, ITsMultiString
    {
        var wsHandle = ResolveWs(cache, writingSystemTag, kind);
        accessor.set_String(wsHandle, TsStringUtils.MakeString(text, wsHandle));
    }

    /// <summary>
    /// Clears one writing-system alternative by setting it to the empty string — LibLCM's own
    /// convention for "no value," per <c>LexSenseSnapshotter</c>'s pre-MOT-4 remarks ("Empty is
    /// indistinguishable from absent; omit rather than write \"\"") and unchanged by MOT-4's
    /// snapshotters, which still omit an empty alternative entirely on read-back.
    /// </summary>
    public static void ApplyClear<TAccessor>(
        LcmCache cache, TAccessor accessor, string writingSystemTag, string kind)
        where TAccessor : IMultiAccessorBase, ITsMultiString
    {
        var wsHandle = ResolveWs(cache, writingSystemTag, kind);
        accessor.set_String(wsHandle, TsStringUtils.MakeString(string.Empty, wsHandle));
    }

    private static int ResolveWs(LcmCache cache, string writingSystemTag, string kind)
    {
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(writingSystemTag);
        if (wsHandle == 0)
        {
            throw new InvalidOperationException(
                $"'{kind}' operation: writing system tag '{writingSystemTag}' is not known to this project.");
        }

        return wsHandle;
    }
}
