using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NodeCanvasUsages.Core.Model;
using NUnit.Framework;

namespace NodeCanvasUsages.Core.Tests;

/// <summary>Byte-for-byte comparison with the expected files stored in <c>testData/golden</c>.</summary>
[TestFixture]
public class GraphRewriterTests
{
    private static void AssertMatchesGolden(string golden, Func<string, string> rewrite)
    {
        var expected = Directory.Exists(TestData.Golden(golden))
            ? TestData.UnityFiles(TestData.Golden(golden)).ToHashSet()
            : new HashSet<string>();
        foreach (var rel in TestData.UnityFiles(TestData.Project))
        {
            var original = TestData.Read(Path.Combine(TestData.Project, rel));
            var actual = GraphRewriter.Rewrite(original, rewrite);
            if (expected.Contains(rel))
            {
                Assert.That(actual, Is.Not.Null, $"{rel} should change");
                Assert.That(actual, Is.EqualTo(TestData.Read(Path.Combine(TestData.Golden(golden), rel))), rel);
            }
            else
            {
                Assert.That(actual, Is.Null, $"{rel} must not be touched");
            }
        }
    }

    [Test]
    public void RenameMethodIncludingRecoveryState() =>
        AssertMatchesGolden("rename-heal", GraphRewriter.RenameMember(new[] { "Demo.Game.Bot" }, "Heal", "Restore"));

    [Test]
    public void RenameProperty() =>
        AssertMatchesGolden("rename-isdead", GraphRewriter.RenameMember(new[] { "Demo.Game.Bot" }, "IsDead", "IsDestroyed"));

    [Test]
    public void RenameMethodInBoundGraph() =>
        AssertMatchesGolden("rename-attack", GraphRewriter.RenameMember(new[] { "Demo.Game.Bot" }, "Attack", "Strike"));

    [Test]
    public void RenameTypeNamespace() =>
        AssertMatchesGolden("type-bot-ns", GraphRewriter.RenameType("Demo.Game.Bot", "Demo.Bots.Bot"));

    [Test]
    public void MoveNestedTypeOut() =>
        AssertMatchesGolden("type-sensor", GraphRewriter.RenameType("Demo.Game.Bot+Sensor", "Demo.Game.Radar"));

    [Test]
    public void RenameField()
    {
        // No golden file for field renames; expected result is the same file with one string changed
        var rel = "Assets/Graphs/Sub_BT.asset";
        var original = TestData.Read(Path.Combine(TestData.Project, rel));
        var actual = GraphRewriter.Rewrite(original, GraphRewriter.RenameMember(new[] { "Demo.Game.Bot" }, "speed", "moveSpeed"));
        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.Replace("Demo.Game.Bot|moveSpeed\"", "Demo.Game.Bot|speed\""), Is.EqualTo(original));
        Assert.That(GraphScanner.Scan(actual, rel).Count(u => u.MemberName == "moveSpeed"), Is.EqualTo(1));
    }

    [Test]
    public void OtherTypesAndPrefixesAreNotRenamed()
    {
        var f = GraphRewriter.RenameMember(new[] { "Demo.Game.Bot" }, "Heal", "Restore");
        Assert.That(f("Demo.Game.Unit|Heal|System.Void"), Is.EqualTo("Demo.Game.Unit|Heal|System.Void"));
        Assert.That(f("My.Demo.Game.Bot|Heal|System.Void"), Is.EqualTo("My.Demo.Game.Bot|Heal|System.Void"));
        Assert.That(f("Demo.Game.Bot|HealAll|System.Void"), Is.EqualTo("Demo.Game.Bot|HealAll|System.Void"));
        Assert.That(f("Demo.Game.Bot|get_Heal|System.Int32"), Is.EqualTo("Demo.Game.Bot|get_Restore|System.Int32"));
    }

    [Test]
    public void RenameTypeKeepsLongerNamesAndQualifiesAssembly()
    {
        var f = GraphRewriter.RenameType("Demo.Game.Bot", "Demo.Bots.Bot", "Bots");
        Assert.That(f("Demo.Game.BotFactory"), Is.EqualTo("Demo.Game.BotFactory"));
        Assert.That(f("Demo.Game.Bot+Sensor|Ping|System.Boolean"), Is.EqualTo("Demo.Bots.Bot+Sensor|Ping|System.Boolean"));
        Assert.That(f("Variable`1[[Demo.Game.Bot, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]"),
            Is.EqualTo("Variable`1[[Demo.Bots.Bot, Bots, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]"));
    }

    [Test]
    public void MovedTypeLeavesNoOldNames()
    {
        // what the "update NodeCanvas graphs" quick-fix does after Move type to outer scope
        foreach (var rel in TestData.UnityFiles(TestData.Project))
        {
            var text = GraphRewriter.Rewrite(TestData.Read(Path.Combine(TestData.Project, rel)),
                GraphRewriter.RenameType("Demo.Game.Bot+Sensor", "Demo.Game.Sensor")) ?? "";
            Assert.That(GraphScanner.ScanTypeNames(text, rel), Has.No.Member("Demo.Game.Bot+Sensor"), rel);
        }
    }

    [Test]
    public void TrimmedEditIsMinimal()
    {
        var edit = new TextEdit(2, 9, "XabZYcd").Trim("01abcabcd9");
        Assert.That(edit.Start, Is.EqualTo(2));
        Assert.That(GraphRewriter.Apply("01abcabcd9", new[] { edit }), Is.EqualTo("01XabZYcd9"));
    }
}
