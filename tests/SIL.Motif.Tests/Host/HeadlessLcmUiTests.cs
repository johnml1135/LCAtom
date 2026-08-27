using System.ComponentModel;
using SIL.Motif.Host.LcmUtils;
using Xunit;

namespace SIL.Motif.Tests.Host;

/// <summary>Covers the headless host's answers to LibLCM's interactive callbacks.</summary>
public sealed class HeadlessLcmUiTests
{
    [Fact]
    public void AConflictingSaveFailsLoudlyRatherThanSilentlyDiscardingTheWork()
    {
        var ui = new HeadlessLcmUi(new NoopInvoker());

        var failure = Record.Exception(() => ui.ConflictingSave());

        // Answering it at all is the hazard: true means revert, and Save then returns as though it worked.
        Assert.IsType<NotSupportedException>(failure);
        Assert.Contains("conflict", failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoopInvoker : ISynchronizeInvoke
    {
        public bool InvokeRequired => false;
        public IAsyncResult BeginInvoke(Delegate method, object?[]? args) => throw new NotSupportedException();
        public object EndInvoke(IAsyncResult result) => throw new NotSupportedException();
        public object Invoke(Delegate method, object?[]? args) => method.DynamicInvoke(args)!;
    }
}
