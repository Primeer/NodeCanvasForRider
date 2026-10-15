using System.Collections.Generic;
using System.IO;
using System.Linq;
using NodeCanvasUsages.Core.Model;
using NUnit.Framework;

namespace NodeCanvasUsages.Core.Tests;

[TestFixture]
public class GraphScannerTests
{
    private static List<(string Path, GraphUsage Usage)> ScanProject()
    {
        var result = new List<(string, GraphUsage)>();
        foreach (var rel in TestData.UnityFiles(TestData.Project))
        {
            var text = TestData.Read(Path.Combine(TestData.Project, rel));
            foreach (var usage in GraphScanner.Scan(text, rel, e => Assert.Fail(e.Message)))
                result.Add((rel, usage));
        }

        return result;
    }

    private static string[] Find(string type, string member) =>
        ScanProject()
            .Where(x => x.Usage.TypeName == type && x.Usage.PlainMemberName == member)
            .Select(x => Describe(x.Path, x.Usage))
            .ToArray();

    // path | owner label | title | kind | member(params) | agent | missing
    private static string Describe(string path, GraphUsage u) =>
        $"{path}{(u.IsBound ? ":" + u.BoundOwner : "")} {u.OwnerLabel} | {u.NodeTitle} | {u.Kind}{(u.IsWrite ? "!" : "")} | " +
        $"{u.MemberName}({u.ParamsInfo}) | {u.Agent ?? "-"} | {u.MissingType ?? "-"} | {u.GraphKind}";

    [Test]
    public void Overloads()
    {
        Assert.That(Find("Demo.Game.Bot", "DistanceTo"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Bot_BT.asset #10 | ConditionNode | Check | DistanceTo(Demo.Game.Unit) | Self | - | BehaviourTree",
            "Assets/Graphs/Bot_BT.asset #11 | ConditionNode | Check | DistanceTo(UnityEngine.Vector3) | Self | - | BehaviourTree",
        }));
    }

    [Test]
    public void PropertyGetterInNodeAndFsmTransition()
    {
        Assert.That(Find("Demo.Game.Bot", "IsDead"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Bot_BT.asset #1 | ConditionNode | Check | get_IsDead() | Self | - | BehaviourTree",
            "Assets/Graphs/Bot_FSM.asset #1->#0 | FSMConnection from NestedBTState \"Combat\" | Check | get_IsDead() | Self | - | StateMachine",
        }));
        Assert.That(Find("Demo.Game.Bot", "Stamina"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Bot_BT.asset #12 | ActionNode | Get | get_Stamina() | Self | - | BehaviourTree",
        }));
    }

    [Test]
    public void BoundGraphInPrefab()
    {
        Assert.That(Find("Demo.Game.Bot", "Attack"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Bot_BT.asset #5 | ActionNode | Call | Attack(Demo.Game.Unit) | Self | - | BehaviourTree",
            "Assets/Prefabs/Sentry.prefab:Sentry #2 | ActionNode \"Shoot\" | Call | Attack(Demo.Game.Unit) | - | - | BehaviourTree",
        }));
        var bound = ScanProject().Single(x => x.Usage.PlainMemberName == "IsAlive").Usage;
        Assert.That(bound.GraphName, Is.EqualTo("Sentry:Sentry"));
        Assert.That(bound.Presentation, Is.EqualTo("Sentry:Sentry #3 — ConditionNode"));
    }

    [Test]
    public void BaseClassMethodOnDerivedTypeAndMissingWrapper()
    {
        Assert.That(Find("Demo.Game.Bot", "Heal"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Bot_BT.asset #9 | ActionNode \"Heal when it's quiet, a long name that pushes the folded scalar past eighty columns for sure\" | Call | Heal(System.Single) | Self | - | BehaviourTree",
            "Assets/Graphs/Sub_BT.asset #5 | ActionNode | Call | Heal(System.Single) | - | Demo.Game.HealAction | BehaviourTree",
        }));
    }

    [Test]
    public void FieldAndNestedType()
    {
        Assert.That(Find("Demo.Game.Bot", "speed"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Sub_BT.asset #3 | ActionNode \"Set speed\" | Field! | speed() | - | - | BehaviourTree",
        }));
        Assert.That(Find("Demo.Game.Bot+Sensor", "Ping"), Is.EqualTo(new[]
        {
            "Assets/Graphs/Sub_BT.asset #4 | ConditionNode | Check | Ping(Demo.Game.Bot) | - | - | BehaviourTree",
        }));
    }

    [Test]
    public void OffsetPointsAtBaseInfoValue()
    {
        foreach (var (path, usage) in ScanProject().Where(x => x.Usage.MissingType == null))
        {
            var text = TestData.Read(Path.Combine(TestData.Project, path));
            var prefix = usage.TypeName.Length > 10 ? usage.TypeName.Substring(0, 10) : usage.TypeName;
            Assert.That(text.Substring(usage.Offset, prefix.Length + 1), Is.EqualTo("\"" + prefix), usage.ToString());
        }
    }

    [Test]
    public void FilesWithoutMarkerAreSkipped()
    {
        Assert.That(GraphScanner.Scan("%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: A\n", "a.prefab"), Is.Empty);
    }

    [Test]
    public void DialogueTreeAndBrokenGraph()
    {
        var all = ScanProject();
        Assert.That(all.Where(x => x.Path.EndsWith("Broken_BT.asset")).Select(x => x.Usage.MemberName),
            Is.EqualTo(new[] { "Fly" }));
        Assert.That(all.Count, Is.EqualTo(14), string.Join("\n", all.Select(x => Describe(x.Path, x.Usage))));
    }

    [Test]
    public void TypeNames()
    {
        var names = TestData.UnityFiles(TestData.Project)
            .SelectMany(rel => GraphScanner.ScanTypeNames(TestData.Read(Path.Combine(TestData.Project, rel)), rel,
                e => Assert.Fail(e.Message)))
            .Distinct()
            .ToList();
        Assert.That(names, Is.SupersetOf(new[]
        {
            "Demo.Game.Bot", // _baseInfo, generic argument of Variable`1
            "Demo.Game.Bot+Sensor", // nested
            "Demo.Game.Unit", // _paramsInfo
            "Demo.Game.HealAction", // _missingType and $type inside _recoveryState
            "Demo.Game.Teleport",
            "NodeCanvas.Framework.Variable`1",
            "NodeCanvas.Tasks.Actions.ExecuteFunction_Multiplatform",
            "System.Boolean",
        }));
        // member names, assembly qualifiers and plain text are not types
        Assert.That(names, Has.None.Matches<string>(n => n.Contains("=") || n.Contains("-") || n.Contains("Heal") && !n.EndsWith("HealAction")));
        Assert.That(names, Has.No.Member("DistanceTo"));
    }

    [Test]
    public void AssemblyNamesAreNotTypeNames()
    {
        var json = "{\"$type\":\"NodeCanvas.Framework.Variable`1[[Company.Game.Bot, Company.Game, Version=0.0.0.0, " +
                   "Culture=neutral, PublicKeyToken=null]]\",\"_type\":\"UnityEngine.Vector3, UnityEngine.CoreModule\"," +
                   "\"_propertyPath\":\"Company.Game.Bot.health\"}";
        var text = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  _serializedGraph: '" + json + "'\n";
        Assert.That(GraphScanner.ScanTypeNames(text, "a.asset", e => Assert.Fail(e.Message)), Is.EqualTo(new[]
        {
            "Company.Game.Bot", "NodeCanvas.Framework.Variable`1", "UnityEngine.Vector3",
        }));
    }

    [Test]
    public void FsJsonAcceptsFullSerializerEscapes()
    {
        // fsJsonPrinter.EscapeString writes \a and \0
        var node = (Json.JsonObject)Json.FsJson.Parse("{\"_name\":\"a\\ab\\0c\"}");
        Assert.That(node.GetString("_name"), Is.EqualTo("a\ab\0c"));
        Assert.That(Json.FsJson.Quote("a\ab\0c"), Is.EqualTo("\"a\\ab\\0c\""));
    }

    [Test]
    public void ClrShortName()
    {
        Assert.That(GraphScanner.ClrShortName("Demo.Game.Bot+Sensor"), Is.EqualTo("Sensor"));
        Assert.That(GraphScanner.ClrShortName("Demo.Pool`1"), Is.EqualTo("Pool`1"));
        Assert.That(GraphScanner.ClrShortName("Bot"), Is.EqualTo("Bot"));
    }
}
