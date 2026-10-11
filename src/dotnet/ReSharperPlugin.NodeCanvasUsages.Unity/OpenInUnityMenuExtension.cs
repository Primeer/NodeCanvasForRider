using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Parts;
using JetBrains.Application.UI.Controls.BulbMenu.Anchors;
using JetBrains.Application.UI.Controls.BulbMenu.Items;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Plugins.Unity.Rider.Integration.Protocol;
using JetBrains.ReSharper.Psi;
using JetBrains.UI.RichText;
using JetBrains.Util;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Rider.CodeVision;

namespace ReSharperPlugin.NodeCanvasUsages.Unity;

/// <summary>
/// "Open in Unity" items in the Code Vision popup: select and ping the graph's asset in the connected Unity editor,
/// through the Unity plugin's own backend/Unity protocol. Without a connected editor no item is shown.
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class OpenInUnityMenuExtension : IGraphUsageMenuExtension
{
    private readonly ISolution mySolution;
    private readonly BackendUnityHost myBackendUnityHost;

    public OpenInUnityMenuExtension(ISolution solution, BackendUnityHost backendUnityHost)
    {
        mySolution = solution;
        myBackendUnityHost = backendUnityHost;
    }

    public IEnumerable<BulbMenuItem> CreateItems(IReadOnlyList<GraphUsageEntry> usages)
    {
        var model = myBackendUnityHost.BackendUnityModel.Value;
        if (model == null || !myBackendUnityHost.IsConnectionEstablished())
            yield break;

        foreach (var file in usages.Select(u => u.SourceFile).Distinct())
        {
            var path = UnityPath(file);
            if (path == null)
                continue;
            yield return new BulbMenuItem(new ExecutableItem(() => model.ShowFileInUnity.Fire(path)),
                new RichText($"Open {file.GetLocation().Name} in Unity"), null,
                BulbMenuAnchors.PermanentBackgroundItems);
        }
    }

    /// <summary>Project-relative path with forward slashes, as Unity's <c>ShowFileInUnity</c> expects.</summary>
    private string? UnityPath(IPsiSourceFile file)
    {
        var relative = file.GetLocation().TryMakeRelativeTo(mySolution.SolutionDirectory);
        return relative == null || relative.IsEmpty ? null : relative.Components.Join("/");
    }
}
