using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

[CollectionDefinition("starved-pool", DisableParallelization = true)]
public sealed class StarvedPoolCollection { }

/// <summary>
/// Holds every thread of the pool until it is disposed and stops the pool from growing, so that nothing queued to the pool runs meanwhile. A real
/// pool is never quite that stuck, but a CI runner with two cores and several test projects running at once gets near it for seconds (the pool adds
/// about one thread every half second, and a few tests keep threads asleep for a minute). Use it for the shortest time you can, and only in the
/// "starved-pool" collection, which runs alone.
/// </summary>
internal sealed class PoolHog : IDisposable
{
    private readonly int _oldWorkers, _oldIo;
    private volatile bool _released;
    private int _running;

    public PoolHog()
    {
        ThreadPool.GetMaxThreads(out _oldWorkers, out _oldIo);
        ThreadPool.GetMinThreads(out int min, out _);
        int threads = Math.Max(min, ThreadPool.ThreadCount);
        // The pool may not grow any more, and every thread it can have is held: whatever is queued now runs only after Dispose.
        if (!ThreadPool.SetMaxThreads(threads, _oldIo)) throw new InvalidOperationException("the pool's limit could not be lowered");
        // More items than threads: a thread that is busy with something else and frees up takes one of these, not the work of the test.
        for (int i = 0; i < threads + 8; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => { Interlocked.Increment(ref _running); while (!_released) Thread.Sleep(5); }, null);
        // Full when items are still waiting and no thread has started one for a while.
        var end = Environment.TickCount64 + 30000;
        int last = -1;
        long since = Environment.TickCount64;
        while (true)
        {
            int now = Volatile.Read(ref _running);
            if (now != last) { last = now; since = Environment.TickCount64; }
            else if (ThreadPool.PendingWorkItemCount > 0 && Environment.TickCount64 - since > 200) break;
            if (Environment.TickCount64 > end) { Dispose(); throw new TimeoutException("the pool did not fill up"); }
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        _released = true;
        ThreadPool.SetMaxThreads(_oldWorkers, _oldIo);
    }
}

/// <summary>
/// The time a module gets for its constructor, Probe, Initialize and Shutdown is time the module runs, not time spent waiting for a free pool
/// thread: a healthy module must not be faulted because the pool was busy.
/// </summary>
[Collection("starved-pool")]
public class StarvedPoolTests
{
    [Fact]
    public void A_module_is_loaded_probed_enabled_and_shut_down_while_the_thread_pool_has_no_free_thread()
    {
        using var env = new TestEnv();
        env.AddModule("sample");
        using var hog = new PoolHog();
        using var host = env.NewHost(timeout: TimeSpan.FromSeconds(2));

        host.Discover();
        var m = host.Find("sample")!;
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
        Assert.True(m.Enable());
        Assert.Equal(ModuleLifecycle.Running, m.Lifecycle);

        host.Shutdown();
        Assert.Equal(ModuleLifecycle.Off, m.Lifecycle);
    }
}
