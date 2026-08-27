namespace SIL.Motif.Contract.Responses;

/// <summary>The <c>open</c> report: the identity of a project and its lexicon's size.</summary>
public sealed record ProjectSummaryProjection(string ProjectName, int LexicalEntryCount);
