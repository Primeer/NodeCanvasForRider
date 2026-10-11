using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.ReSharper.TestFramework;
using NodeCanvasUsages.Core.Model;
using NUnit.Framework;
using ReSharperPlugin.NodeCanvasUsages.Index;

namespace ReSharperPlugin.NodeCanvasUsages.Tests;

/// <summary>Resolution of graph references (from the shared testData project) to C# members.</summary>
[TestFixture]
public class UsageResolverTests : BaseTestWithSingleProject
{
    protected override string RelativeTestDataPath => "Resolver";

    /// <summary>Index over the testData/project assets, scanned directly (no Unity plugin in tests).</summary>
    internal sealed class FixtureIndex : IGraphUsageIndex
    {
        private readonly List<GraphUsageEntry> myEntries = new();

        public FixtureIndex()
        {
            var root = ProjectRoot();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".asset") || f.EndsWith(".prefab") || f.EndsWith(".unity"))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(path, new UTF8Encoding(false));
                foreach (var usage in GraphScanner.Scan(text, Path.GetFileName(path)))
                    myEntries.Add(new GraphUsageEntry(null!, usage));
            }
        }

        public bool IsEnabled => true;

        public IReadOnlyList<GraphUsageEntry> GetByMemberName(string plainMemberName) =>
            myEntries.Where(e => e.Usage.PlainMemberName == plainMemberName).ToList();

        public static string ProjectRoot()
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "testData", "project");
                if (Directory.Exists(candidate))
                    return candidate;
            }

            throw new DirectoryNotFoundException("testData/project");
        }
    }

    private void Check(Func<ITypeElement, IEnumerable<IDeclaredElement>> select, string type, params string[] expected)
    {
        WithSingleProject(new[] { "Bot.cs", "Stubs.cs" }, (_, solution, _) =>
        {
            using (ReadLockCookie.Create())
            {
                var resolver = new NodeCanvasUsageResolver(solution, new FixtureIndex());
                var typeElement = solution.GetPsiServices().Symbols
                    .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true).GetTypeElementByCLRName(type);
                Assert.That(typeElement, Is.Not.Null, type);
                var actual = select(typeElement!)
                    .SelectMany(e => resolver.FindUsages(e, includeBound: true))
                    .Select(e => $"{e.Usage.Presentation} [{e.Usage.MemberName}({e.Usage.ParamsInfo})]")
                    .ToArray();
                Assert.That(actual, Is.EqualTo(expected));
            }
        });
    }

    private static Func<ITypeElement, IEnumerable<IDeclaredElement>> Methods(string name, int parameters = -1) =>
        t => t.Methods.Where(m => m.ShortName == name && (parameters < 0 || m.Parameters.Count == parameters));

    private static Func<ITypeElement, IEnumerable<IDeclaredElement>> Member(string name) =>
        t => t.GetMembers().Where(m => m.ShortName == name);

    [Test]
    public void OverloadsAreSeparate()
    {
        Check(t => t.Methods.Where(m => m.ShortName == "DistanceTo" && m.Parameters[0].Type.GetPresentableName(JetBrains.ReSharper.Psi.CSharp.CSharpLanguage.Instance!) == "Unit"),
            "Demo.Game.Bot", "Bot_BT #10 — ConditionNode [DistanceTo(Demo.Game.Unit)]");
        Check(t => t.Methods.Where(m => m.ShortName == "DistanceTo" && m.Parameters[0].Type.GetPresentableName(JetBrains.ReSharper.Psi.CSharp.CSharpLanguage.Instance!) == "Vector3"),
            "Demo.Game.Bot", "Bot_BT #11 — ConditionNode [DistanceTo(UnityEngine.Vector3)]");
    }

    [Test]
    public void OverloadWithoutGraphUsageHasNone()
    {
        Check(Methods("MoveTo", 2), "Demo.Game.Bot");
        Check(Methods("MoveTo", 1), "Demo.Game.Bot", "Bot_BT #7 — ActionNode [MoveTo(UnityEngine.Vector3)]");
    }

    [Test]
    public void PropertyFromNodeAndTransition() =>
        Check(Member("IsDead"), "Demo.Game.Bot",
            "Bot_BT #1 — ConditionNode [get_IsDead()]",
            "Bot_FSM #1->#0 — FSMConnection from NestedBTState \"Combat\" [get_IsDead()]");

    [Test]
    public void BaseClassMethodCalledOnDerivedTypeIncludingMissingWrapper() =>
        Check(Methods("Heal"), "Demo.Game.Pawn",
            "Bot_BT #9 — ActionNode \"Heal when it's quiet, a long name that pushes the folded scalar past eighty columns for sure\" [Heal(System.Single)]",
            "Sub_BT #5 — ActionNode [Heal(System.Single)]");

    [Test]
    public void BoundGraph() =>
        Check(Methods("IsAlive"), "Demo.Game.Pawn", "Sentry:Sentry #3 — ConditionNode [IsAlive()]");

    [Test]
    public void FieldAndNestedType()
    {
        Check(Member("speed"), "Demo.Game.Bot", "Sub_BT #3 — ActionNode \"Set speed\" [speed()]");
        Check(Methods("Ping"), "Demo.Game.Bot+Sensor", "Sub_BT #4 — ConditionNode [Ping(Demo.Game.Bot)]");
    }

    [Test]
    public void UnusedMemberHasNone() => Check(Methods("CanSee"), "Demo.Game.Bot");
}
