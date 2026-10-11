using System.Threading;
using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.TestFramework;
using JetBrains.TestFramework.Application.Zones;
using NUnit.Framework;

#if NETFRAMEWORK
// STA is only available on Windows; tests run on .NET (macOS/Linux) without it
[assembly: Apartment(ApartmentState.STA)]
#endif

namespace ReSharperPlugin.NodeCanvasUsages.Tests;

[ZoneDefinition]
public class NodeCanvasUsagesTestEnvironmentZone : ITestsEnvZone, IRequire<PsiFeatureTestZone>, IRequire<INodeCanvasUsagesZone>;
