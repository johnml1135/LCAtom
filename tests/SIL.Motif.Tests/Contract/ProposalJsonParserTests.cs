using SIL.Motif.Contract.Parsing;
using Xunit;

namespace SIL.Motif.Tests.Contract;

/// <summary>
/// Strict closed JSON parsing: valid documents parse into the expected DTO shape, and every class
/// of malformed input the Change Set contract classifies as a hard error — unknown top-level or
/// operation properties, unknown operation kinds, malformed or duplicate ids, a contract-versions
/// mismatch with the operations actually used, more than one creator operation for an entity id,
/// and a non-integral number where the schema requires one — is rejected rather than silently
/// ignored or truncated.
/// </summary>
public class ProposalJsonParserTests
{
    private static string MinimalValidProposal(string proposalId = TestIds.Proposal1) => $$"""
        {
          "contractVersions": { "lexical": "1.0" },
          "proposalId": "{{proposalId}}",
          "requires": [],
          "operations": [
            {
              "operationId": "{{TestIds.Op1}}",
              "kind": "lexical/entry/create",
              "entityId": "{{TestIds.Entity1}}",
              "after": { "headword": "run" },
              "rationale": "Corpus evidence",
              "confidence": 0.92,
              "provenance": [],
              "extensions": {}
            }
          ],
          "extensions": {}
        }
        """;

    [Fact]
    public void Parse_MinimalValidProposal_Succeeds()
    {
        var proposal = ProposalJsonParser.Parse(MinimalValidProposal());

        Assert.Equal(TestIds.Proposal1, proposal.ProposalId.Value);
        Assert.Single(proposal.Operations);
        Assert.Empty(proposal.Requires);

        var operation = proposal.Operations[0];
        Assert.Equal("lexical/entry/create", operation.Kind);
        Assert.Equal(TestIds.Entity1, operation.EntityId!.Value.Value);
        Assert.Equal("Corpus evidence", operation.Rationale);
        Assert.Equal(0.92, operation.Confidence);
    }

    [Fact]
    public void Parse_RequiresArray_ParsesMultipleEntriesInAuthoredOrder()
    {
        var json = $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "requires": ["{{TestIds.Right1}}", "{{TestIds.Left1}}"],
              "operations": [
                {
                  "operationId": "{{TestIds.Op1}}",
                  "kind": "sequence/move",
                  "target": "{{TestIds.Entity1}}",
                  "placement": { "after": "{{TestIds.Left1}}" }
                }
              ]
            }
            """;

        var proposal = ProposalJsonParser.Parse(json);

        Assert.Equal(2, proposal.Requires.Count);
        // The parser preserves authored order; only digest projection sorts unordered members.
        Assert.Equal(TestIds.Right1, proposal.Requires[0].Value);
        Assert.Equal(TestIds.Left1, proposal.Requires[1].Value);
    }

    [Fact]
    public void Parse_RequiresEntryEqualToOwnProposalId_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "requires": ["{{TestIds.Proposal1}}"],
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "sequence/move", "target": "{{TestIds.Entity1}}", "placement": { "after": "{{TestIds.Left1}}" } }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("cannot list its own proposalId", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateRequiresEntry_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "requires": ["{{TestIds.Left1}}", "{{TestIds.Left1}}"],
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "sequence/move", "target": "{{TestIds.Entity1}}", "placement": { "after": "{{TestIds.Left1}}" } }
              ]
            }
            """;

        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_UnknownTopLevelProperty_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [],
              "unknownTopLevelThing": 1
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Unknown property 'unknownTopLevelThing'", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOperationProperty_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                {
                  "operationId": "{{TestIds.Op1}}",
                  "kind": "lexical/entry/create",
                  "entityId": "{{TestIds.Entity1}}",
                  "totallyMadeUpProperty": "boo"
                }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Unknown property 'totallyMadeUpProperty'", ex.Message);
    }

    [Fact]
    public void Parse_UnknownOperationKind_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "made-up-group": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "made-up-group/nonsense", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Unknown operation kind", ex.Message);
    }

    [Fact]
    public void Parse_PaddedProposalId_IsRejected()
    {
        var json = MinimalValidProposal(TestIds.Proposal1.Substring(0, TestIds.Proposal1.Length - 2) + "==");
        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_WrongLengthOperationId_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "agent_tooShort", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_ContractVersionsMissingUsedGroup_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "wrongGroup": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("missing an entry for group 'lexical'", ex.Message);
    }

    [Fact]
    public void Parse_ContractVersionsDeclaresUnusedGroup_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0", "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("declares group 'sequence'", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateOperationId_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" },
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity2}}" }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Duplicate operationId", ex.Message);
    }

    [Fact]
    public void Parse_EntityIdWithTwoCreatorOperations_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" },
                { "operationId": "{{TestIds.Op2}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("more than one creator operation", ex.Message);
    }

    [Fact]
    public void Parse_FloatValueInAfterPayload_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}",
                  "after": { "someCustomWeight": 1.5 } }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Floating-point value", ex.Message);
    }

    [Fact]
    public void Parse_IntegralNumberInAfterPayload_IsAccepted()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}",
                  "after": { "someCount": 42 } }
              ]
            }
            """;

        var proposal = ProposalJsonParser.Parse(json);
        Assert.Equal(42, proposal.Operations[0].After!.Value.GetProperty("someCount").GetInt32());
    }

    [Fact]
    public void Parse_ExtensionsMustBeObject_ArrayIsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" }
              ],
              "extensions": [1, 2, 3]
            }
            """;

        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_PlacementWithNeitherSide_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "sequence/move", "target": "{{TestIds.Entity1}}", "placement": {} }
              ]
            }
            """;

        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_PlacementWithUnknownProperty_IsRejected()
    {
        var json = $$"""
            {
              "contractVersions": { "sequence": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "sequence/move", "target": "{{TestIds.Entity1}}",
                  "placement": { "after": "{{TestIds.Left1}}", "index": 3 } }
              ]
            }
            """;

        var ex = Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
        Assert.Contains("Unknown property 'index'", ex.Message);
    }

    [Fact]
    public void Parse_ConfidenceMustBeNumber()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}",
                  "confidence": "high" }
              ]
            }
            """;

        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse(json));
    }

    [Fact]
    public void Parse_DependsOn_ParsesAndDeduplicates()
    {
        var json = $$"""
            {
              "contractVersions": { "lexical": "1.0" },
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": [
                { "operationId": "{{TestIds.Op1}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity1}}" },
                { "operationId": "{{TestIds.Op2}}", "kind": "lexical/entry/create", "entityId": "{{TestIds.Entity2}}",
                  "dependsOn": ["{{TestIds.Op1}}"] }
              ]
            }
            """;

        var proposal = ProposalJsonParser.Parse(json);
        var dependsOn = proposal.Operations[1].DependsOn;
        Assert.Single(dependsOn);
        Assert.Equal(TestIds.Op1, dependsOn[0].OperationId.Value);
    }

    [Fact]
    public void Parse_NotAJsonObject_IsRejected()
    {
        Assert.Throws<ContractParseException>(() => ProposalJsonParser.Parse("[1,2,3]"));
    }

    [Fact]
    public void Parse_OperationsMayBeEmpty()
    {
        var json = $$"""
            {
              "contractVersions": {},
              "proposalId": "{{TestIds.Proposal1}}",
              "operations": []
            }
            """;

        var proposal = ProposalJsonParser.Parse(json);
        Assert.Empty(proposal.Operations);
    }
}
