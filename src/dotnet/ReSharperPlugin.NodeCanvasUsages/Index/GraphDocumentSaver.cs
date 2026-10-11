using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel.Storage;
using JetBrains.ProjectModel;
using JetBrains.Util;
using JetBrains.Util.Logging;

namespace ReSharperPlugin.NodeCanvasUsages.Index;

/// <summary>
/// Keeps graph assets and C# consistent on disk. After a rename the edited assets can be written at once while the
/// renamed C# stays in unsaved editors; Unity then reloads graphs that reference members its compiled code does not
/// have (red "missing" nodes). So all documents are saved together right after the refactoring.
/// Undo needs nothing: it leaves both the C# and the assets unsaved, and they are saved together later.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class GraphDocumentSaver
{
    private static readonly ILogger ourLogger = Logger.GetLogger<GraphDocumentSaver>();

    private readonly ISolution mySolution;
    private int myQueued;

    public GraphDocumentSaver(ISolution solution) => mySolution = solution;

    public void RequestSaveAll(string reason)
    {
        if (Interlocked.Exchange(ref myQueued, 1) == 1)
            return;

        var storage = mySolution.TryGetComponent<IDocumentStorageHelpers>();
        if (storage == null)
        {
            Interlocked.Exchange(ref myQueued, 0);
            ourLogger.Warn("NodeCanvas: IDocumentStorageHelpers is not available, documents are not saved");
            return;
        }

        mySolution.Locks.ReentrancyGuard.Queue("NodeCanvas: save documents", () =>
        {
            Interlocked.Exchange(ref myQueued, 0);
            storage.SaveAllDocuments();
            ourLogger.Verbose("NodeCanvas: saved all documents ({0})", reason);
        });
    }
}
