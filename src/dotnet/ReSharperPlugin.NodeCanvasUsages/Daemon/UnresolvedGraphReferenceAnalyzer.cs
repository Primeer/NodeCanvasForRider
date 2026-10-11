using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;

namespace ReSharperPlugin.NodeCanvasUsages.Daemon;

[RegisterConfigurableSeverity(SeverityId, null, HighlightingGroupIds.CodeSmell, Title, Description, Severity.WARNING)]
[ConfigurableSeverityHighlighting(SeverityId, CSharpLanguage.Name, OverlapResolve = OverlapResolveKind.NONE)]
public class UnresolvedGraphReferenceWarning : IHighlighting
{
    public const string SeverityId = "NodeCanvasUnresolvedGraphReference";
    private const string Title = "NodeCanvas graph references a missing member";
    private const string Description =
        "A NodeCanvas graph calls a method, property or field of this type that no longer exists, " +
        "or whose parameters no longer match the graph (the task will fail to deserialize in Unity).";

    private readonly ITreeNode myNode;
    private readonly DocumentRange myRange;

    public UnresolvedGraphReferenceWarning(ITreeNode node, DocumentRange range, string message)
    {
        myNode = node;
        myRange = range;
        ToolTip = message;
    }

    public string ToolTip { get; }
    public string ErrorStripeToolTip => ToolTip;
    public bool IsValid() => myNode.IsValid();
    public DocumentRange CalculateRange() => myRange;
}

/// <summary>
/// Reports graph references (<c>_baseInfo</c>) to members of a type that no longer resolve: the member was removed
/// or renamed outside of Rider's Rename, or its signature changed so the stored overload is gone. Shown on the member
/// with that name if one exists, otherwise on the type.
/// </summary>
[ElementProblemAnalyzer(typeof(IClassLikeDeclaration))]
public class UnresolvedGraphReferenceAnalyzer : ElementProblemAnalyzer<IClassLikeDeclaration>
{
    private const int MaxLocations = 3;
    private readonly NodeCanvasGraphCache myCache;

    public UnresolvedGraphReferenceAnalyzer(NodeCanvasGraphCache cache) => myCache = cache;

    protected override void Run(IClassLikeDeclaration element, ElementProblemAnalyzerData data,
                                IHighlightingConsumer consumer)
    {
        var type = element.DeclaredElement;
        if (type == null || !myCache.IsEnabled)
            return;
        var usages = myCache.GetByTypeName(type.GetClrName().FullName);
        if (usages.Count == 0)
            return;

        var broken = usages
            .Where(u => NodeCanvasUsageResolver.ResolveMember(type, u.Usage) == null)
            .GroupBy(u => (u.Usage.MemberName, u.Usage.ParamsInfo, u.Usage.Kind == UsageKind.Field));
        foreach (var group in broken)
        {
            var usage = group.First().Usage;
            var sameName = element.MemberDeclarations
                .FirstOrDefault(m => m.DeclaredElement?.ShortName == usage.PlainMemberName);
            var signature = usage.Kind == UsageKind.Field || usage.IsPropertyAccessor
                ? ""
                : $"({string.Join(", ", (usage.ParamsInfo ?? "").Split('|').Where(p => p.Length > 0).Select(ShortName))})";
            var what = sameName != null && signature.Length > 0
                ? $"overload {type.ShortName}.{usage.PlainMemberName}{signature} that no longer exists"
                : $"missing member {type.ShortName}.{usage.MemberName}{signature}";
            var locations = group.Take(MaxLocations).Select(u => u.Usage.Presentation).ToList();
            if (group.Count() > MaxLocations)
                locations.Add($"and {group.Count() - MaxLocations} more");
            var message = $"NodeCanvas graph references {what}: {string.Join("; ", locations)}";

            var node = (ITreeNode?)sameName ?? element;
            var range = sameName is IDeclaration declaration
                ? declaration.GetNameDocumentRange()
                : element.GetNameDocumentRange();
            consumer.AddHighlighting(new UnresolvedGraphReferenceWarning(node, range, message));
        }
    }

    private static string ShortName(string clrName) => GraphScanner.ShortTypeName(clrName);
}
