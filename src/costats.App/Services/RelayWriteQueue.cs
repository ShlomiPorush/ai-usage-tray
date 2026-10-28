namespace costats.App.Services
{
    /// <summary>
    /// The one serial line every relay write waits in: snapshot uploads and
    /// deletes, usage uploads and deletes. A write starts only after the one
    /// queued before it has finished, so an older write can never land after
    /// a newer one.
    /// </summary>
    public sealed class RelayWriteQueue
    {
        private readonly object _sync = new();
        private Task _tail = Task.CompletedTask;

        /// <summary>
        /// Queues <paramref name="work"/> behind every write queued so far. A
        /// failed earlier write does not stop it. The work runs on the thread
        /// pool, never inline in the caller.
        /// </summary>
        public Task<T> Enqueue<T>(Func<Task<T>> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            lock (_sync)
            {
                var next = _tail
                    .ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                    .Unwrap();
                _tail = next;
                return next;
            }
        }

        /// <inheritdoc cref="Enqueue{T}(Func{Task{T}})"/>
        public Task Enqueue(Func<Task> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            return Enqueue(async () =>
            {
                await work().ConfigureAwait(false);
                return true;
            });
        }

        /// <summary>Completes once every write queued so far has finished, failed or not.</summary>
        public Task WhenIdleAsync()
        {
            Task tail;
            lock (_sync)
            {
                tail = _tail;
            }

            return tail.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
