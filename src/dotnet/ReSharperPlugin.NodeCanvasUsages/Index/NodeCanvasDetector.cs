using System;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>
/// Tells whether the solution uses NodeCanvas (its <c>NodeCanvas.Framework.Graph</c> type is visible either as
/// source or as a referenced assembly). Without it the plugin does no work at all.
/// A negative answer is only trusted once the PSI caches are built; it is re-checked whenever they become idle
/// and at most every 30 seconds otherwise.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasDetector
{
    public const string GraphTypeName = "NodeCanvas.Framework.Graph";

    private static readonly TimeSpan ourNegativeRecheck = TimeSpan.FromSeconds(30);

    private readonly ISolution mySolution;
    private readonly IShellLocks myLocks;
    private readonly IPsiCachesState myCachesState;
    private readonly ILogger myLogger;
    private readonly object myLock = new();
    private volatile bool myFound;
    private bool myReportedMissing;
    private DateTime myLastCheck = DateTime.MinValue;

    public NodeCanvasDetector(Lifetime lifetime, ISolution solution, IShellLocks locks, IPsiCachesState cachesState,
                              ILogger logger)
    {
        mySolution = solution;
        myLocks = locks;
        myCachesState = cachesState;
        myLogger = logger;

        cachesState.IsIdle.Change.Advise(lifetime, args =>
        {
            if (args.HasNew && args.New && !myFound)
                locks.ExecuteOrQueueReadLockEx(lifetime, "NodeCanvas detection", () => Check(force: true));
        });
    }

    /// <summary>Raised when NodeCanvas is found after it had been reported missing.</summary>
    public event Action? Appeared;

    public bool IsNodeCanvasProject => myFound || Check(force: false);

    private bool Check(bool force)
    {
        if (myFound)
            return true;

        // until the caches are built a missing type means nothing; say "no" without remembering it
        var cachesReady = myCachesState.IsInitialUpdateFinished.Value;
        lock (myLock)
        {
            if (!force && cachesReady && DateTime.UtcNow - myLastCheck < ourNegativeRecheck)
            {
                myReportedMissing = true;
                return false;
            }

            if (cachesReady)
                myLastCheck = DateTime.UtcNow;
        }

        if (!Lookup())
        {
            lock (myLock)
                myReportedMissing = true;
            myLogger.Verbose("NodeCanvas: {0} not found (caches ready: {1})", GraphTypeName, cachesReady);
            return false;
        }

        bool notify;
        lock (myLock)
        {
            myFound = true;
            notify = myReportedMissing;
        }

        myLogger.Info("NodeCanvas: found {0}, graph indexing is active", GraphTypeName);
        if (notify)
            Appeared?.Invoke();
        return true;
    }

    private bool Lookup()
    {
        if (myLocks.IsReadAccessAllowed())
            return LookupUnderLock();
        using (ReadLockCookie.Create())
            return LookupUnderLock();
    }

    private bool LookupUnderLock()
    {
        var scope = mySolution.GetPsiServices().Symbols.GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true);
        return scope.GetTypeElementByCLRName(GraphTypeName) != null;
    }
}
