using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>
/// Re-runs code analysis (Code Vision, unused-symbol warnings) after the graph index changes. While the deferred
/// caches are still processing files the refresh is postponed until they finish, so a cold start triggers it once.
/// </summary>
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
public class NodeCanvasDaemonRefresher
{
    private readonly Lifetime myLifetime;
    private readonly IShellLocks myLocks;
    private readonly IDaemon myDaemon;
    private readonly DeferredCacheController myController;
    private int myDirty;

    public NodeCanvasDaemonRefresher(Lifetime lifetime, NodeCanvasGraphCache cache, DeferredCacheController controller,
                                     IShellLocks locks, IDaemon daemon)
    {
        myLifetime = lifetime;
        myLocks = locks;
        myDaemon = daemon;
        myController = controller;

        cache.Changed += OnChanged;
        lifetime.OnTermination(() => cache.Changed -= OnChanged);
        controller.IsProcessing.Change.Advise(lifetime, args =>
        {
            if (args.HasNew && !args.New)
                Flush();
        });
    }

    private void OnChanged()
    {
        System.Threading.Interlocked.Exchange(ref myDirty, 1);
        if (!myController.IsProcessing.Value)
            Flush();
    }

    private void Flush()
    {
        if (System.Threading.Interlocked.Exchange(ref myDirty, 0) == 0)
            return;
        myLocks.ExecuteOrQueueEx(myLifetime, "NodeCanvas usages: refresh analysis",
            () => myDaemon.Invalidate("NodeCanvas graph index changed"));
    }
}
