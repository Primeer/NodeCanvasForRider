using System.Linq;
using JetBrains.Application.changes;
using JetBrains.Application.Parts;
using JetBrains.Application.Progress;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.DataFlow;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.Util;
using ReSharperPlugin.NodeCanvasUsages.Settings;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>
/// Rebuilds <see cref="NodeCanvasGraphCache"/> when its applicability changes (NodeCanvas appears in the solution,
/// or indexing is switched on or off): the cache is invalidated and every asset file is re-queued, as
/// resharper-unity's <c>UnityAssetsCacheUpdater</c> does for its own cache.
/// </summary>
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
public class NodeCanvasGraphCacheUpdater
{
    private readonly Lifetime myLifetime;
    private readonly ISolution mySolution;
    private readonly DeferredCacheController myController;
    private readonly IShellLocks myLocks;
    private readonly ChangeManager myChangeManager;

    public NodeCanvasGraphCacheUpdater(Lifetime lifetime, ISolution solution, DeferredCacheController controller,
                                       NodeCanvasDetector detector, IShellLocks locks, ChangeManager changeManager,
                                       ISettingsStore settingsStore)
    {
        myLifetime = lifetime;
        mySolution = solution;
        myController = controller;
        myLocks = locks;
        myChangeManager = changeManager;

        detector.Appeared += Rebuild;
        lifetime.OnTermination(() => detector.Appeared -= Rebuild);

        settingsStore.BindToContextLive(lifetime, ContextRange.Smart(solution.ToDataContext()))
            .GetValueProperty(lifetime, (NodeCanvasSettings s) => s.Enabled)
            .Change.Advise_NoAcknowledgement(lifetime, args =>
            {
                if (args.HasOld && args.Old != args.New)
                    Rebuild();
            });
    }

    private void Rebuild()
    {
        myLocks.ExecuteOrQueueReadLockEx(myLifetime, "NodeCanvas graph cache rebuild", () =>
        {
            var files = mySolution.GetPsiServices().Modules.GetModules()
                .SelectMany(m => m.SourceFiles)
                .Where(NodeCanvasGraphCache.IsAssetFile)
                .ToList();

            myLocks.ExecuteWithWriteLock(() =>
            {
                myController.Invalidate<NodeCanvasGraphCache>();
                if (files.Count == 0)
                    return;

                myChangeManager.ExecuteAfterChange(() =>
                {
                    var builder = new PsiModuleChangeBuilder();
                    foreach (var file in files)
                    {
                        if (file.IsValid())
                            builder.AddFileChange(file, PsiModuleChange.ChangeType.Modified);
                    }

                    myChangeManager.OnProviderChanged(mySolution, builder.Result, SimpleTaskExecutor.Instance);
                });
            });
        });
    }
}
