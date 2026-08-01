using System;
using SIL.LCModel;
using SIL.LCModel.Core.Text;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// Lowers a <see cref="LexicalSenseOperationKinds.SetGloss"/> operation's payload into the one
/// LibLCM write it performs: <c>sense.Gloss.set_String(wsHandle, ...)</c>.
/// </summary>
/// <remarks>
/// Must run inside an already-open unit of work (see
/// docs/adr/0006-engine-reality-apply-readback-preflight.md, decision 5: never call a bare
/// <c>UndoableUnitOfWorkHelper.Do</c> from lowering, and never nest a unit of work) — the caller
/// (<see cref="SIL.Motif.Runner.DryRun.ProposalDryRunner"/> in Stage C) owns opening and
/// closing/rolling back that unit of work.
/// </remarks>
public static class SetGlossLowering
{
    public static void Apply(LcmCache cache, ILexSense sense, string writingSystemTag, string text)
    {
        var wsHandle = cache.WritingSystemFactory.GetWsFromStr(writingSystemTag);
        if (wsHandle == 0)
        {
            throw new InvalidOperationException(
                $"Writing system tag '{writingSystemTag}' is not known to this project.");
        }

        sense.Gloss.set_String(wsHandle, TsStringUtils.MakeString(text, wsHandle));
    }
}
