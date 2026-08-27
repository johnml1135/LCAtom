using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SIL.Motif.Contract.Responses;

/// <summary>Why one command refused, in a form a caller can branch on.</summary>
/// <remarks>
/// A closed set, because a reason is a promise. A consumer that meets one it does not recognise falls
/// back to the class its exit code names, which is why the retry decision lives on the code and not here.
/// </remarks>
public enum FailureReason
{
    /// <summary>The invocation was malformed: an absent flag, or a value that is not what it claims.</summary>
    InvalidArgument,

    /// <summary>The request named something that is not there.</summary>
    NotFound,

    /// <summary>Well-formed, and refused: a failed precondition, a policy denial, or drift.</summary>
    Refused,

    /// <summary>Well-formed, and not attemptable now: a held lock, a busy store, a lease elsewhere.</summary>
    Busy,

    /// <summary>The store disagrees with itself. Not the caller's doing, and not fixed by retrying.</summary>
    StoreInconsistent,
}

/// <summary>The single object a failed command emits under <c>--json</c>.</summary>
public sealed record FailureEnvelope
{
    [JsonConstructor]
    public FailureEnvelope(FailureReason reason, string message,
        IReadOnlyDictionary<string, string>? detail = null)
    {
        Reason = reason;
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A failure message is required.", nameof(message))
            : message;
        Detail = detail;
    }

    /// <summary>Always false, so a reader that captured both streams can tell them apart.</summary>
    [JsonPropertyOrder(0)] public bool Ok => false;

    [JsonPropertyOrder(1)] public FailureReason Reason { get; }

    /// <summary>The same wording a human sees, unchanged.</summary>
    [JsonPropertyOrder(2)] public string Message { get; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Detail { get; }

    /// <summary>The process exit code this reason maps to.</summary>
    /// <remarks>
    /// Kept beside the reason so the two cannot drift. The split that earns its keep is
    /// <see cref="FailureReason.Refused"/> against <see cref="FailureReason.Busy"/>: an agent must not
    /// retry a refusal, and must be free to retry a lock.
    /// </remarks>
    public static int ExitCodeFor(FailureReason reason) => reason switch
    {
        FailureReason.InvalidArgument => 1,
        FailureReason.NotFound => 2,
        FailureReason.Refused => 2,
        FailureReason.Busy => 3,
        FailureReason.StoreInconsistent => 4,
        _ => 4,
    };
}
