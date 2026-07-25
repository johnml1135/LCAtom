using SIL.LCAtom.Contract.Ids;

namespace SIL.LCAtom.Contract.Model;

/// <summary>
/// An explicit intra-Change-Set ordering dependency: this operation requires
/// <see cref="OperationId"/> to have already executed earlier in the same Change Set's
/// <c>operations</c> array. This is distinct from the Change Set-level <c>requires</c>
/// (docs/change-set-contract.md, "Prerequisites"), which names other Change Sets that must already
/// be in the applied history.
/// </summary>
/// <remarks>
/// The set of an operation's dependencies is unordered for digest purposes (see
/// docs/adr/0007-cross-language-digest-determinism.md decision 2); the natural ordering
/// authored in <c>operations</c> already carries the primary sequencing intent.
/// </remarks>
public sealed record OperationDependency(CanonicalId OperationId);
