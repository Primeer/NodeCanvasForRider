using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.Application.UI.Options;
using JetBrains.Application.UI.Options.OptionPages;
using JetBrains.Application.UI.Options.OptionsDialog;
using JetBrains.IDE.UI.Options;
using JetBrains.Lifetimes;
using JetBrains.ReSharper.Feature.Services.OptionPages.CodeEditing;
using JetBrains.ReSharper.Feature.Services.Resources;
using ReSharperPlugin.NodeCanvasUsages.Settings;

namespace ReSharperPlugin.NodeCanvasUsages.Rider.Options;

[ZoneMarker]
public class ZoneMarker : IRequire<IToolsOptionsPageImplZone>;

[OptionsPage(PID, "NodeCanvas Usages", typeof(CodeInsightsThemedIcons.InsightWait), ParentId = CodeEditingPage.PID)]
public class NodeCanvasOptionsPage : BeSimpleOptionsPage
{
    public const string PID = "NodeCanvasUsagesSettings";

    public NodeCanvasOptionsPage(Lifetime lifetime, OptionsPageContext optionsPageContext,
                                 OptionsSettingsSmartContext optionsSettingsSmartContext)
        : base(lifetime, optionsPageContext, optionsSettingsSmartContext)
    {
        AddKeyword("NodeCanvas", "Behaviour Tree", "FSM", "Dialogue Tree", "graph");
        AddText("Usages of C# members in NodeCanvas graphs (Behaviour Trees, FSMs, Dialogue Trees).");
        AddBoolOption((NodeCanvasSettings s) => s.Enabled, "Index NodeCanvas graphs and show their usages");
        AddBoolOption((NodeCanvasSettings s) => s.IncludeBoundGraphs,
            "Include graphs bound to GraphOwner components (prefabs and scenes)");
        AddCommentText("Graph assets are read through the Unity plugin; asset indexing must be enabled there.");
    }
}
