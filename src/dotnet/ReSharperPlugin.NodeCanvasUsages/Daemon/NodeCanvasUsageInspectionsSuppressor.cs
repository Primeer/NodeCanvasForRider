using System.Linq;
using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.UsageChecking;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeAnnotations;
using JetBrains.Util;
using NodeCanvasUsages.Core.Model;
using ReSharperPlugin.NodeCanvasUsages.Index;

namespace ReSharperPlugin.NodeCanvasUsages.Daemon;

/// <summary>
/// Members referenced by NodeCanvas graphs are used implicitly (through reflection), so they are not reported as
/// unused, like Unity event functions in resharper-unity. Getters/<c>GetField</c> count as reads, setters and
/// <c>SetField</c> as writes.
/// </summary>
[ShellComponent(Instantiation.DemandAnyThreadSafe)]
public class NodeCanvasUsageInspectionsSuppressor : IUsageInspectionsSuppressor
{
    public ImplicitUseFlags SuppressUsageInspectionsOnElement(IDeclaredElement element)
    {
        var member = NodeCanvasUsageResolver.Normalize(element);
        if (member == null)
            return ImplicitUseFlags.Empty;

        var solution = element.GetSolution();
        var cache = solution.TryGetComponent<NodeCanvasGraphCache>();
        if (cache == null || !cache.IsEnabled || !cache.HasMemberName(member.ShortName))
            return ImplicitUseFlags.Empty;

        // all graphs count, whatever the Code Vision display setting says
        var usages = solution.GetComponent<NodeCanvasUsageResolver>().FindUsages(member, includeBound: true);
        if (usages.Count == 0)
            return ImplicitUseFlags.Empty;

        var kind = (ImplicitUseKindFlags)0;
        if (usages.Any(u => u.Usage.IsWrite || u.Usage.Kind == UsageKind.Set))
            kind |= ImplicitUseKindFlags.Assign;
        if (usages.Any(u => !u.Usage.IsWrite && u.Usage.Kind != UsageKind.Set))
            kind |= ImplicitUseKindFlags.Access;
        return new ImplicitUseFlags(kind, ImplicitUseTargetFlags.Itself, default);
    }
}
