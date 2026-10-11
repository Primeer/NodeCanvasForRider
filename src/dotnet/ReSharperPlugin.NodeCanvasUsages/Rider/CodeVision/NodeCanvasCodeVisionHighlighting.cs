using System.Collections.Generic;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Daemon.CodeInsights;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Rider.Model;
using JetBrains.TextControl.DocumentMarkup;
using Severity = JetBrains.ReSharper.Feature.Services.Daemon.Severity;
using ReSharperPlugin.NodeCanvasUsages.Index;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.CodeVision;

[RegisterHighlighter(Id,
    GroupId = HighlighterGroupIds.HIDDEN,
    EffectType = EffectType.NONE,
    Layer = HighlighterLayer.SYNTAX + 1,
    NotRecyclable = true,
    TransmitUpdates = true)]
[StaticSeverityHighlighting(Severity.INFO, typeof(HighlightingGroupIds.CodeInsights), AttributeId = Id,
    OverlapResolve = OverlapResolveKind.NONE)]
public class NodeCanvasCodeVisionHighlighting : CodeInsightsHighlighting
{
    private new const string Id = "NodeCanvasUsagesCodeInsights";

    public NodeCanvasCodeVisionHighlighting(DocumentRange range, string text, string tooltip,
                                            ICodeInsightsProvider provider, IDeclaredElement element,
                                            IconModel? icon, IReadOnlyList<GraphUsageEntry> usages)
        : base(range, text, tooltip, text, provider, element, icon, new List<CodeVisionEntryExtraActionModel>())
    {
        Usages = usages;
    }

    public IReadOnlyList<GraphUsageEntry> Usages { get; }
}
