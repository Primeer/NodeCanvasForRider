using JetBrains.Application.Parts;
using JetBrains.Application.UI.Icons.CommonThemedIcons;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Feature.Services.Resources;
using JetBrains.ReSharper.Feature.Services.Tree;
using JetBrains.ReSharper.Psi;
using JetBrains.Rider.Backend.Features.Usages;
using JetBrains.Rider.Backend.Platform.Icons;
using JetBrains.Rider.Model;
using JetBrains.UI.Icons;
using JetBrains.Util;
using ReSharperPlugin.NodeCanvasUsages.Search;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.Usages;

/// <summary>Groups graph usages in Rider's Find Usages tree (like resharper-unity's GameObject/Component rules).</summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasExtraGroupingRulesProvider : IRiderExtraGroupingRulesProvider
{
    public NodeCanvasExtraGroupingRulesProvider(IconHost iconHost) =>
        ExtraRules = new IRiderUsageGroupingRule[] { new NodeCanvasGraphGroupingRule(iconHost) };

    public IRiderUsageGroupingRule[] ExtraRules { get; }
}

/// <summary>
/// "NodeCanvas graph" level for graphs bound to GraphOwner components: a prefab or scene may hold several, so they
/// are grouped by owner GameObject. A graph asset is its own file, so it gets no extra level.
/// </summary>
public class NodeCanvasGraphGroupingRule : IRiderUsageGroupingRule
{
    private readonly IconHost myIconHost;

    public NodeCanvasGraphGroupingRule(IconHost iconHost) => myIconHost = iconHost;

    // CamelCase: the frontend inserts spaces for the toolbar ("Node Canvas Graph")
    public string RuleId => "NodeCanvasGraph";
    public string Name => "NodeCanvasGraph";
    public IconId IconId => CodeInsightsThemedIcons.InsightWait.Id;
    public bool IsSeparable => true;
    public bool IsNavigateable => false;
    public bool Configurable => true;
    public bool PriorityDependsOnUsages => true;

    // after directory and file, like resharper-unity's GameObject rule
    public double SortingPriority => 7.0;
    public bool DefaultValue => true;

    public RdUsageGroup CreateModel(IOccurrence occurrence, IOccurrenceBrowserDescriptor descriptor,
                                    bool forInspectionResult, bool forUsagesPopup) =>
        occurrence is NodeCanvasOccurrence { Usage.BoundOwner: { } owner }
            ? new RdUsageGroup(RuleId, owner, myIconHost.Transform(IconId))
            : new RdUsageGroup(RuleId, string.Empty, null);

    public void Navigate(IOccurrence occurrence)
    {
    }

    public IDeclaredElement? GetDeclaredElement(IOccurrence occurrence) => null;
    public IProjectItem? GetProjectItem(IOccurrence occurrence) => null;
}

/// <summary>Directory and file levels for graph usages (assets are not project items).</summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasUsageProjectItemProvider : ProjectItemRule.ICustomProjectItemProvider
{
    private readonly ISolution mySolution;
    private readonly ProjectModelIcons myProjectModelIcons;
    private readonly IconHost myIconHost;

    public NodeCanvasUsageProjectItemProvider(ISolution solution, ProjectModelIcons projectModelIcons, IconHost iconHost)
    {
        mySolution = solution;
        myProjectModelIcons = projectModelIcons;
        myIconHost = iconHost;
    }

    public RdUsageGroupTextAndIcon? GetUsageGroup(IOccurrence occurrence, ProjectItemKind kind, bool takeParent)
    {
        if (occurrence is not NodeCanvasOccurrence o)
            return null;

        var location = o.SourceFile.GetLocation();
        switch (kind)
        {
            case ProjectItemKind.PROJECT:
                return new RdUsageGroupTextAndIcon("", null);
            case ProjectItemKind.PHYSICAL_FILE:
                var file = Relative(location);
                return file == null
                    ? null
                    : new RdUsageGroupTextAndIcon(file, myIconHost.Transform(CommonThemedIcons.Document.Id));
            case ProjectItemKind.PHYSICAL_DIRECTORY:
                var directory = Relative(location.Directory);
                return directory == null
                    ? null
                    : new RdUsageGroupTextAndIcon(directory, myIconHost.Transform(myProjectModelIcons.DirectoryIcon));
            default:
                return null;
        }
    }

    private string? Relative(VirtualFileSystemPath path) =>
        mySolution.SolutionDirectory.IsPrefixOf(path) ? path.MakeRelativeTo(mySolution.SolutionDirectory).FullPath : null;
}
