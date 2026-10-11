using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Plugins.Unity.Rider;

namespace ReSharperPlugin.NodeCanvasUsages.Unity;

/// <summary>Loaded only when the Unity Support plugin is present.</summary>
[ZoneMarker]
public class ZoneMarker : IRequire<IRiderUnityPluginZone>;
