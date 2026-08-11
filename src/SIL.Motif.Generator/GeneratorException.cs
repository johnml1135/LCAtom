namespace SIL.Motif.Generator;

/// <summary>
/// The one exception type the generator throws for every fail-closed check: the (Class, Field)
/// join, a manifest row disagreeing with a derivation (ADR 0022 decision 3), an unrecognized
/// class-name prefix (ADR 0024 decision 2), or a create/delete kind naming an abstract class
/// (ADR 0023 decision 2). One type is deliberate — every caller of this project treats "the
/// generator threw" as "stop and fail the build," and there is no case here where a caller needs
/// to distinguish which check fired programmatically rather than by reading the message.
/// </summary>
public sealed class GeneratorException : Exception
{
    public GeneratorException(string message) : base(message)
    {
    }

    public GeneratorException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
