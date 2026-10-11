using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi.CSharp;

namespace ReSharperPlugin.NodeCanvasUsages;

/// <summary>Everything the plugin needs; activated by tests, while in the IDE the requirements below are enough.</summary>
[ZoneDefinition]
public interface INodeCanvasUsagesZone : IZone, IRequire<ILanguageCSharpZone>, IRequire<DaemonZone>;
