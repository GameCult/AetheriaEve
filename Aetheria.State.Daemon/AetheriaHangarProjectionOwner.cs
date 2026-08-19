using System;
using System.Threading;
using System.Threading.Tasks;

namespace Aetheria.State.Daemon;

internal sealed record AetheriaPreparedHangarProjection(
    string UpdatedAtUtc,
    AetheriaProgressionVerseView View);

/// <summary>
/// Owns Hangar projection ordering. Reads may be slow and remote, but a prepared
/// candidate may commit only if no Hangar mutation or newer refresh request
/// overtook it.
/// </summary>
internal sealed class AetheriaHangarProjectionOwner<TCandidate> : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _finalityGate = new(1, 1);
    private readonly Func<CancellationToken, Task<TCandidate>> _prepare;
    private readonly Func<TCandidate, CancellationToken, Task> _commit;
    private readonly Action<Exception> _reportFailure;
    private readonly CancellationTokenSource _shutdown = new();
    private Task _worker = Task.CompletedTask;
    private long _requestedGeneration;
    private long _committedGeneration;
    private bool _disposed;

    public AetheriaHangarProjectionOwner(
        Func<CancellationToken, Task<TCandidate>> prepare,
        Func<TCandidate, CancellationToken, Task> commit,
        Action<Exception>? reportFailure = null)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _reportFailure = reportFailure ?? (_ => { });
    }

    public void RequestRefresh()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _requestedGeneration++;
            EnsureWorkerLocked();
        }
    }

    public async ValueTask<IAsyncDisposable> EnterMutationAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _finalityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MutationLease(this);
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task worker;
            lock (_sync)
            {
                worker = _worker;
                if (worker.IsCompleted && _committedGeneration >= _requestedGeneration)
                    return;
            }
            await worker.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
            worker = _worker;
        }
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        _shutdown.Dispose();
        _finalityGate.Dispose();
    }

    private void EndMutation()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _requestedGeneration++;
                EnsureWorkerLocked();
            }
        }
        _finalityGate.Release();
    }

    private void EnsureWorkerLocked()
    {
        if (_worker.IsCompleted)
            _worker = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            long generation;
            lock (_sync)
            {
                if (_disposed || _committedGeneration >= _requestedGeneration)
                    return;
                generation = _requestedGeneration;
            }

            TCandidate candidate;
            try
            {
                candidate = await _prepare(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _reportFailure(error);
                lock (_sync)
                    _committedGeneration = Math.Max(_committedGeneration, generation);
                continue;
            }

            await _finalityGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (_disposed)
                        return;
                    if (generation != _requestedGeneration)
                        continue;
                }

                await _commit(candidate, _shutdown.Token).ConfigureAwait(false);
                lock (_sync)
                    _committedGeneration = Math.Max(_committedGeneration, generation);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _reportFailure(error);
                lock (_sync)
                    _committedGeneration = Math.Max(_committedGeneration, generation);
            }
            finally
            {
                _finalityGate.Release();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AetheriaHangarProjectionOwner<TCandidate>));
    }

    private sealed class MutationLease : IAsyncDisposable
    {
        private AetheriaHangarProjectionOwner<TCandidate>? _owner;

        public MutationLease(AetheriaHangarProjectionOwner<TCandidate> owner) => _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.EndMutation();
            return ValueTask.CompletedTask;
        }
    }
}
