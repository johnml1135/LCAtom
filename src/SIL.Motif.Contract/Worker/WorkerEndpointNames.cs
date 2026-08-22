using System;

namespace SIL.Motif.Contract.Worker;

/// <summary>Derives the stable per-user names shared by worker owners and clients.</summary>
public static class WorkerEndpointNames
{
    /// <summary>Returns the control pipe name for a validated user namespace.</summary>
    public static string ControlPipe(string userNamespace)
    {
        Validate(userNamespace);
        return "motif-worker-" + userNamespace;
    }

    /// <summary>Returns the ownership mutex name for a validated user namespace.</summary>
    public static string OwnerMutex(string userNamespace)
    {
        Validate(userNamespace);
        return "Global\\MotifWorkerOwner-" + userNamespace;
    }

    private static void Validate(string userNamespace)
    {
        if (string.IsNullOrWhiteSpace(userNamespace) || userNamespace.Length > 256 ||
            userNamespace.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
            throw new ArgumentException("The worker user namespace is invalid.", nameof(userNamespace));
    }
}
