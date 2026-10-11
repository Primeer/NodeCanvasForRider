using JetBrains.Application.Settings;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using ReSharperPlugin.NodeCanvasUsages.Index;
using ReSharperPlugin.NodeCanvasUsages.Settings;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.CodeVision;

[ElementProblemAnalyzer(typeof(IMethodDeclaration), typeof(IPropertyDeclaration), typeof(IFieldDeclaration))]
public class NodeCanvasCodeVisionAnalyzer : ElementProblemAnalyzer<ICSharpDeclaration>
{
    private readonly NodeCanvasGraphCache myCache;
    private readonly NodeCanvasUsageResolver myResolver;
    private readonly NodeCanvasCodeVisionProvider myProvider;

    public NodeCanvasCodeVisionAnalyzer(NodeCanvasGraphCache cache, NodeCanvasUsageResolver resolver,
                                        NodeCanvasCodeVisionProvider provider)
    {
        myCache = cache;
        myResolver = resolver;
        myProvider = provider;
    }

    protected override void Run(ICSharpDeclaration element, ElementProblemAnalyzerData data,
                                IHighlightingConsumer consumer)
    {
        var declared = element.DeclaredElement;
        if (declared == null || !myCache.IsEnabled || !myCache.HasMemberName(declared.ShortName))
            return;

        var includeBound = data.SettingsStore.GetValue((NodeCanvasSettings s) => s.IncludeBoundGraphs);
        var usages = myResolver.FindUsages(declared, includeBound);
        if (usages.Count == 0)
            return;

        var tooltip = usages.Count == 1
            ? NodeCanvasCodeVisionProvider.MenuText(usages[0].Usage)
            : "Click to see usages in NodeCanvas graphs";
        consumer.AddHighlighting(new NodeCanvasCodeVisionHighlighting(element.GetNameDocumentRange(),
            NodeCanvasCodeVisionProvider.Text(usages.Count), tooltip, myProvider, declared, null, usages));
    }
}
