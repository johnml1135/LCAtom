using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Parsing;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Intent digest determinism: identical authored intent always yields an identical digest; fields
/// the Change Set contract classifies as excluded (proposalId, rationale, confidence,
/// provenance, extensions, pretty-printing) never move it; unordered members may be authored in
/// any order without moving it; but the authoritative operation order does move it when changed.
/// </summary>
public class IntentDigestTests
{
    private static string Digest(string json) => IntentDigest.Compute(ProposalJsonParser.Parse(json));

    private static readonly string BaseProposal = $$"""
        {
          "contractVersions": { "lexical": "1.0" },
          "proposalId": "{{TestIds.Proposal1}}",
          "operations": [
            { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}", "after": { "headword": "run" } }
          ]
        }
        """;

    [Fact]
    public void DigestFormat_IsSha256PrefixPlusLowercaseHex()
    {
        var digest = Digest(BaseProposal);
        Assert.Matches("^sha256:[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void SameIntent_ParsedTwice_ProducesSameDigest()
    {
        Assert.Equal(Digest(BaseProposal), Digest(BaseProposal));
    }

    [Fact]
    public void ProposalId_DoesNotAffectDigest()
    {
        var withDifferentId = BaseProposal.Replace(TestIds.Proposal1, TestIds.Proposal2);

        Assert.Equal(Digest(BaseProposal), Digest(withDifferentId));
    }

    [Fact]
    public void Rationale_Confidence_Provenance_Extensions_DoNotAffectDigest()
    {
        var withoutReviewMetadata = BaseProposal;

        var withReviewMetadata = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}",
                  "after": { "headword": "run" },
                  "rationale": "Corpus evidence suggests this gloss",
                  "confidence": 0.99,
                  "provenance": [ { "source": "corpus-42" } ],
                  "extensions": { "toolHint": "authored-in-flexicon" } }
              ],
              "extensions": { "sessionId": "abc-123" }
            }
            """;

        Assert.Equal(Digest(withoutReviewMetadata), Digest(withReviewMetadata));
    }

    [Fact]
    public void PrettyPrinting_DoesNotAffectDigest()
    {
        var compact =
            $$"""{"contractVersions":{"lexical":"1.0"},"proposalId":"{{TestIds.Proposal1}}","operations":[{"operationId":"{{TestIds.Op1}}","kind":"lexical/entry/create","entityId":"{{TestIds.Entity1}}","after":{"headword":"run"} }]}""";

        Assert.Equal(Digest(compact), Digest(BaseProposal));
    }

    [Fact]
    public void ReorderingRequires_DoesNotChangeDigest()
    {
        string ProposalWithRequires(string firstId, string secondId) => $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "requires": ["{{firstId}}", "{{secondId}}"],
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "sequence/move", "target": "{{TestIds.Entity1}}", "placement": { "after": "{{TestIds.Left1}}" } }
              ]
            }
            """;

        var forward = ProposalWithRequires(TestIds.Left1, TestIds.Right1);
        var reversed = ProposalWithRequires(TestIds.Right1, TestIds.Left1);

        Assert.Equal(Digest(forward), Digest(reversed));
    }

    [Fact]
    public void ReorderingDependsOn_DoesNotChangeDigest()
    {
        string ProposalWithDependsOn(string firstDep, string secondDep) => $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" },
                { "operationId": "{{TestIds.Op2}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity2}}" },
                { "operationId": "{{TestIds.Op3}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Right1}}",
                  "dependsOn": ["{{firstDep}}", "{{secondDep}}"] }
              ]
            }
            """;

        var forward = ProposalWithDependsOn(TestIds.Op1, TestIds.Op2);
        var reversed = ProposalWithDependsOn(TestIds.Op2, TestIds.Op1);

        Assert.Equal(Digest(forward), Digest(reversed));
    }

    [Fact]
    public void ReorderingOperations_ChangesDigest()
    {
        string ProposalWithOperationOrder(string firstEntity, string secondEntity) => $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{firstEntity}}" },
                { "operationId": "{{TestIds.Op2}}", "kind": "lexical/entry/create", "entityId": "{{secondEntity}}" }
              ]
            }
            """;

        var forward = ProposalWithOperationOrder(TestIds.Entity1, TestIds.Entity2);
        var reversed = ProposalWithOperationOrder(TestIds.Entity2, TestIds.Entity1);

        Assert.NotEqual(Digest(forward), Digest(reversed));
    }

    [Fact]
    public void StorageIdOverride_ChangesDigest()
    {
        var withoutOverride = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        var withOverride = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}",
                  "storageIdOverride": "{{TestIds.Entity2}}" }
              ]
            }
            """;

        Assert.NotEqual(Digest(withoutOverride), Digest(withOverride));
    }

    [Fact]
    public void DifferentAfterValue_ChangesDigest()
    {
        string ProposalWithHeadword(string headword) => $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}", "after": { "headword": "{{headword}}" } }
              ]
            }
            """;

        Assert.NotEqual(Digest(ProposalWithHeadword("run")), Digest(ProposalWithHeadword("walk")));
    }
}
