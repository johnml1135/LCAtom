using SIL.Motif.Contract.Ids;

namespace SIL.Motif.Contract.Model;

/// <summary>
/// An explicit intra-Change-Set ordering dependency: this operation requires
/// <see cref="OperationId"/> to have already executed earlier in the same Proposal's
/// <c>operations</c> array. This is distinct from the Proposal-level <c>requires</c> (the Change Set
/// contract's "Prerequisites"), which names other Proposals that must already be in the applied
/// history.
/// </summary>
/// <remarks>
/// The set of an operation's dependencies is unordered for digest purposes (ADR 0007 decision 2);
/// the natural ordering authored in <c>operations</c> already carries the primary sequencing
/// intent.
/// </remarks>
public sealed record OperationDependency(CanonicalId OperationId);
