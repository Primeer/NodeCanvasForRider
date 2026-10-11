using JetBrains.Application.Settings;
using JetBrains.ReSharper.Resources.Settings;

namespace ReSharperPlugin.NodeCanvasUsages.Settings;

[SettingsKey(typeof(CodeEditingSettings), "NodeCanvas Usages")]
public class NodeCanvasSettings
{
    [SettingsEntry(true, "Index NodeCanvas graphs and show their usages of C# members")]
    public bool Enabled;

    [SettingsEntry(true, "Include graphs bound to GraphOwner components in prefabs and scenes")]
    public bool IncludeBoundGraphs;
}
