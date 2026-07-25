using System;
using System.Collections.Generic;
using System.Text.Json;
using SIL.LCAtom.Contract.Ids;

namespace SIL.LCAtom.Contract.Model;

/// <summary>
/// The Change Set document envelope. See docs/change-set-contract.md, "Document shape".
/// </summary>
/// <remarks>
/// Semantic (participate in the intent digest): <see cref="ContractVersions"/>,
/// <see cref="Requires"/>, and <see cref="Operations"/> (order is authoritative and hashed).
///
/// Non-semantic (excluded from the intent digest): <see cref="ChangeSetId"/> — uniquely minted at
/// creation, content-independent, frozen (see
/// docs/adr/0004-prerequisite-graph-stable-ids-bound-apply.md) — and <see cref="Extensions"/>.
/// </remarks>
public sealed record ChangeSetEnvelope
{
    public ChangeSetEnvelope(
        IReadOnlyDictionary<string, string> contractVersions,
        CanonicalId changeSetId,
        IReadOnlyList<CanonicalId>? requires,
        IReadOnlyList<OperationEnvelope> operations,
        JsonElement? extensions = null)
    {
        ContractVersions = contractVersions ?? throw new ArgumentNullException(nameof(contractVersions));
        ChangeSetId = changeSetId;
        Requires = requires ?? Array.Empty<CanonicalId>();
        Operations = operations ?? throw new ArgumentNullException(nameof(operations));
        Extensions = extensions;
    }

    /// <summary>
    /// Maps each contract group (the leading segment of an operation's <c>kind</c>) to the
    /// contract major/minor this Change Set was authored against. Must name exactly the groups
    /// <see cref="Operations"/> uses.
    /// </summary>
    public IReadOnlyDictionary<string, string> ContractVersions { get; }

    /// <summary>
    /// A 128-bit id minted when the Change Set was created — content-independent, unique by
    /// construction, and frozen for the Change Set's lifetime. Excluded from the intent digest.
    /// </summary>
    public CanonicalId ChangeSetId { get; }

    /// <summary>
    /// Other Change Sets that must already be in the applied history before this one may apply.
    /// Forms a DAG with other Change Sets' <c>requires</c>; unordered for digest purposes.
    /// </summary>
    public IReadOnlyList<CanonicalId> Requires { get; }

    /// <summary>The authoritative execution order. Never silently reordered by a runner.</summary>
    public IReadOnlyList<OperationEnvelope> Operations { get; }

    /// <summary>Opaque, non-semantic tool data. Excluded from the intent digest.</summary>
    public JsonElement? Extensions { get; }
}
