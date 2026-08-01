using System.Text.Json;
using SIL.Motif.Contract.Parsing;

namespace SIL.Motif.Runner.Operations;

/// <summary>
/// The <c>after</c> payload shape for <see cref="LexicalSenseOperationKinds.SetGloss"/>:
/// <c>{ "ws": "&lt;writing-system tag&gt;", "text": "&lt;desired gloss text&gt;" }</c>.
/// </summary>
public static class SetGlossPayload
{
    public static (string WritingSystemTag, string Text) Parse(JsonElement after)
    {
        if (!after.TryGetProperty("ws", out var wsElement) || wsElement.ValueKind != JsonValueKind.String)
        {
            throw new ContractParseException(
                $"'{LexicalSenseOperationKinds.SetGloss}' operation 'after.ws' is required and must be a string.");
        }

        if (!after.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            throw new ContractParseException(
                $"'{LexicalSenseOperationKinds.SetGloss}' operation 'after.text' is required and must be a string.");
        }

        return (wsElement.GetString()!, textElement.GetString()!);
    }
}
