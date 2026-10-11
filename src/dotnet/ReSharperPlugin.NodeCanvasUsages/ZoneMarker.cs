using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi.CSharp;
#if RIDER
using JetBrains.Rider.Backend.Env;
#endif

namespace ReSharperPlugin.NodeCanvasUsages;

[ZoneMarker]
public class ZoneMarker : IRequire<ILanguageCSharpZone>, IRequire<DaemonZone>
#if RIDER
    , IRequire<IRiderPlatformZone>
#endif
{
}
