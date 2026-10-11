using JetBrains.Application.Parts;
using JetBrains.IDE;
using JetBrains.IDE.Common;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.TextControl;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Search;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.Navigation;

/// <summary>Opens the asset that contains a graph reference, with the caret on its <c>_baseInfo</c>.</summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class GraphUsageNavigator : IGraphUsageNavigation
{
    private readonly IEditorManager myEditorManager;

    public GraphUsageNavigator(IEditorManager editorManager) => myEditorManager = editorManager;

    public void Navigate(GraphUsageEntry entry) => Navigate(entry.SourceFile, entry.Usage.Offset, activate: true);

    public void Navigate(IPsiSourceFile sourceFile, int offset, bool activate)
    {
        var options = (activate ? OpenFileOptions.DefaultActivate : OpenFileOptions.DefaultNoActivate)
            .WithCaretPosition(new DocOffsetAndVirtual((DocOffset)offset));
        myEditorManager.OpenFileAsync(sourceFile.GetLocation(), options);
    }
}
