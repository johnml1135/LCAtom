using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SIL.Motif.Worker;

internal sealed class WorkerMutexOwner : IDisposable
{
    private readonly Mutex _mutex;
    private readonly BlockingCollection<Command> _commands = new BlockingCollection<Command>();
    private readonly Thread _thread;
    private bool _disposed;
    private bool _ownsMutex;

    public WorkerMutexOwner(string name)
    {
        _mutex = new Mutex(false, name);
        _thread = new Thread(Run) { IsBackground = true, Name = "Motif worker mutex owner" };
        _thread.Start();
    }

    public bool TryAcquire()
    {
        return Invoke(() =>
        {
            if (_ownsMutex)
                return true;
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
            return _ownsMutex;
        });
    }

    public void Release()
    {
        Invoke(() =>
        {
            if (!_ownsMutex)
                return true;
            _mutex.ReleaseMutex();
            _ownsMutex = false;
            return true;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Exception? failure = null;
        try
        {
            Release();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        _disposed = true;
        _commands.CompleteAdding();
        _thread.Join();
        _mutex.Dispose();
        if (failure is not null)
            throw new InvalidOperationException("The worker owner mutex could not be released.", failure);
    }

    private T Invoke<T>(Func<T> operation)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorkerMutexOwner));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(new Command(() => completion.TrySetResult(operation()),
            exception => completion.TrySetException(exception)));
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            try { command.Run(); }
            catch (Exception exception) { command.Fail(exception); }
        }
    }

    private sealed class Command
    {
        private readonly Action _run;
        private readonly Action<Exception> _fail;

        public Command(Action run, Action<Exception> fail)
        {
            _run = run;
            _fail = fail;
        }

        public void Run() => _run();

        public void Fail(Exception exception)
        {
            _fail(exception);
        }
    }
}
