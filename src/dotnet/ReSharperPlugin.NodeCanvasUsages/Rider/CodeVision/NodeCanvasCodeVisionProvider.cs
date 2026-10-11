using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Components;
using JetBrains.Application.Parts;
using JetBrains.Application.UI.Controls.BulbMenu.Anchors;
using JetBrains.Application.UI.Controls.BulbMenu.Items;
using JetBrains.Application.UI.Controls.GotoByName;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.ProjectModel;
using JetBrains.RdBackend.Common.Features.CodeInsights.Providers;
using JetBrains.RdBackend.Common.Features.Services;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.Rider.Model;
using JetBrains.UI.RichText;
using JetBrains.Util;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Rider.Navigation;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.CodeVision;

/// <summary>"NodeCanvas: N usages" above methods, properties and fields used by graphs; click lists the usages.</summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasCodeVisionProvider : ICodeInsightsProvider
{
    public const string Id = "NodeCanvas Usages";

    private readonly ILazy<BulbMenuComponent> myBulbMenu;
    private readonly GraphUsageNavigator myNavigator;
    private readonly IEnumerable<IGraphUsageMenuExtension> myExtensions;

    public NodeCanvasCodeVisionProvider(ILazy<BulbMenuComponent> bulbMenu, GraphUsageNavigator navigator,
                                        IEnumerable<IGraphUsageMenuExtension> extensions)
    {
        myBulbMenu = bulbMenu;
        myNavigator = navigator;
        myExtensions = extensions;
    }

    public bool IsAvailableIn(ISolution solution) => true;

    public string ProviderId => Id;
    public string DisplayName => "NodeCanvas usages";
    public CodeVisionAnchorKind DefaultAnchor => CodeVisionAnchorKind.Top;

    public ICollection<CodeVisionRelativeOrdering> RelativeOrderings =>
        new CodeVisionRelativeOrdering[] { new CodeVisionRelativeOrderingBefore(ReferencesCodeInsightsProvider.Id) };

    public static string Text(int count) => $"NodeCanvas: {count} {(count == 1 ? "usage" : "usages")}";

    public static string MenuText(GraphUsage usage)
    {
        var text = usage.Presentation;
        if (usage.MissingType != null)
            text += $" (missing {GraphScanner.ShortTypeName(usage.MissingType)})";
        return text;
    }

    public void OnClick(CodeInsightHighlightInfo highlightInfo, ISolution solution, CodeInsightsClickInfo clickInfo)
    {
        if (highlightInfo.CodeInsightsHighlighting is not NodeCanvasCodeVisionHighlighting highlighting)
            return;

        var items = new List<BulbMenuItem>();
        foreach (var entry in highlighting.Usages)
        {
            var e = entry;
            items.Add(new BulbMenuItem(new ExecutableItem(() => myNavigator.Navigate(e)),
                new RichText(MenuText(e.Usage)), null, BulbMenuAnchors.FirstClassContextItems));
        }

        foreach (var extension in myExtensions)
            items.AddRange(extension.CreateItems(highlighting.Usages));

        var offset = highlighting.Range.StartOffset.Offset;
        myBulbMenu.Value.ShowBulbMenu(items,
            new PopupWindowContextSource(_ => new RiderEditorOffsetPopupWindowContext(offset)), null, null, null);
    }

    public void OnExtraActionClick(CodeInsightHighlightInfo highlightInfo, string actionId, ISolution solution)
    {
    }
}

/// <summary>Adds extra items (e.g. "Open in Unity") to the Code Vision popup.</summary>
public interface IGraphUsageMenuExtension
{
    IEnumerable<BulbMenuItem> CreateItems(IReadOnlyList<GraphUsageEntry> usages);
}
