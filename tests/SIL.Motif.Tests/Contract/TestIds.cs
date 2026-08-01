namespace SIL.Motif.Tests.Contract;

/// <summary>
/// A handful of fixed, valid canonical-id suffixes (16 sequential bytes each, matching the style
/// of the worked examples in docs/change-set-contract.md) shared across the contract kernel
/// tests, so fixtures stay readable and stable instead of relying on randomly minted ids.
/// </summary>
internal static class TestIds
{
    public const string Proposal1 = "agent_AAECAwQFBgcICQoLDA0ODw"; // bytes 0x00..0x0f (doc's own example)
    public const string Proposal2 = "agent_EBESExQVFhcYGRobHB0eHw"; // bytes 0x10..0x1f
    public const string Entity1 = "agent_ICEiIyQlJicoKSorLC0uLw"; // bytes 0x20..0x2f (doc's own example)
    public const string Entity2 = "agent_MDEyMzQ1Njc4OTo7PD0-Pw"; // bytes 0x30..0x3f
    public const string Op1 = "agent_QEFCQ0RFRkdISUpLTE1OTw"; // bytes 0x40..0x4f
    public const string Op2 = "agent_UFFSU1RVVldYWVpbXF1eXw"; // bytes 0x50..0x5f
    public const string Op3 = "agent_YGFiY2RlZmdoaWprbG1ubw"; // bytes 0x60..0x6f
    public const string Left1 = "agent_cHFyc3R1dnd4eXp7fH1-fw"; // bytes 0x70..0x7f
    public const string Right1 = "agent_gIGCg4SFhoeIiYqLjI2Ojw"; // bytes 0x80..0x8f
}
