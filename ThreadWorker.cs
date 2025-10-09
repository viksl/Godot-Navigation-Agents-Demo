using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NavigationSwarmTest;

public class ThreadWorker : IDisposable
{
    private readonly BlockingCollection<WorkItem> _workQueue = new();
    private readonly Thread _workerThread;
    private volatile bool _isRunning = true;

    private class WorkItem
    {
        public Action Action { get; set; }
        public TaskCompletionSource<bool> CompletionSource { get; set; }
    }

    public ThreadWorker(string threadName = "DedicatedWorker")
    {
        _workerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = threadName
        };
        _workerThread.Start();
    }

    private void ProcessQueue()
    {
        while (_isRunning)
        {
            try
            {
                var workItem = _workQueue.Take(); // Blocks until work is available
                try
                {
                    workItem.Action();
                    workItem.CompletionSource.SetResult(true);
                }
                catch (Exception ex)
                {
                    workItem.CompletionSource.SetException(ex);
                }
            }
            catch (InvalidOperationException)
            {
                // Queue was completed
                break;
            }
        }
    }

    public Task EnqueueWork(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        // This below might be the safest approach since the await might be finished on the thread which called the task.
        // Which means it could be this thread on the thread worker, which might be a race condition since the code after
        // the await accesses TestNavigation.cs fields just like other threads might at the same time.
        // But since the continuation code is now called in a deferred manner - it should not matter anymore.
        // var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workQueue.Add(new WorkItem { Action = action, CompletionSource = tcs });
        return tcs.Task;
    }

    public void Dispose()
    {
        _isRunning = false;
        _workQueue.CompleteAdding();
        _workerThread.Join(1000); // Wait up to 1 second for thread to finish
    }
}