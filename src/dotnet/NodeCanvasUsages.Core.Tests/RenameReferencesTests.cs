using System.IO;
using System.Linq;
using System.Text;
using NodeCanvasUsages.Core.Model;
using NUnit.Framework;

namespace NodeCanvasUsages.Core.Tests;

[TestFixture]
public class RenameReferencesTests
{
    private static string? RenameFile(string rel, System.Func<MemberRef, bool> match, string newName)
    {
        var text = TestData.Read(Path.Combine(TestData.Project, rel));
        var edits = GraphRewriter.ComputeJsonEdits(text, json => GraphRewriter.RenameReferences(json, match, newName));
        return edits.Count == 0 ? null : GraphRewriter.Apply(text, edits);
    }

    [TestCase("Heal", "Restore", "rename-heal")]
    [TestCase("IsDead", "IsDestroyed", "rename-isdead")]
    [TestCase("Attack", "Strike", "rename-attack")]
    public void PreciseRenameMatchesGolden(string oldName, string newName, string golden)
    {
        var expected = TestData.UnityFiles(TestData.Golden(golden)).ToHashSet();
        foreach (var rel in TestData.UnityFiles(TestData.Project))
        {
            var actual = RenameFile(rel, r => r.TypeName == "Demo.Game.Bot" && r.PlainMemberName == oldName, newName);
            if (expected.Contains(rel))
                Assert.That(actual, Is.EqualTo(TestData.Read(Path.Combine(TestData.Golden(golden), rel))), rel);
            else
                Assert.That(actual, Is.Null, rel);
        }
    }

    [Test]
    public void RenamingOneOverloadKeepsTheOther()
    {
        var rel = "Assets/Graphs/Bot_BT.asset";
        var actual = RenameFile(rel, r => r.PlainMemberName == "DistanceTo" && r.ParamsInfo == "Demo.Game.Unit", "RangeTo");
        Assert.That(actual, Is.Not.Null);
        var usages = GraphScanner.Scan(actual!, rel);
        Assert.That(usages.Where(u => u.MemberName == "RangeTo").Select(u => u.ParamsInfo), Is.EqualTo(new[] { "Demo.Game.Unit" }));
        Assert.That(usages.Where(u => u.MemberName == "DistanceTo").Select(u => u.ParamsInfo), Is.EqualTo(new[] { "UnityEngine.Vector3" }));
    }

    [Test]
    public void FieldRenameUsesFieldReferencesOnly()
    {
        var actual = RenameFile("Assets/Graphs/Sub_BT.asset", r => r.IsField && r.MemberName == "speed", "moveSpeed");
        Assert.That(actual, Is.Not.Null);
        Assert.That(GraphScanner.Scan(actual!, "Sub_BT.asset").Count(u => u.MemberName == "moveSpeed"), Is.EqualTo(1));
    }

    [Test]
    public void NamespaceRename()
    {
        var f = GraphRewriter.RenameNamespace("Demo.Game", "Demo.Units");
        Assert.That(f("Demo.Game.Bot|Heal|System.Void"), Is.EqualTo("Demo.Units.Bot|Heal|System.Void"));
        Assert.That(f("Demo.Game.Inner.Bot"), Is.EqualTo("Demo.Units.Inner.Bot"));
        Assert.That(f("Demo.GameX.Bot"), Is.EqualTo("Demo.GameX.Bot"));
        Assert.That(f("My.Demo.Game.Bot"), Is.EqualTo("My.Demo.Game.Bot"));
        Assert.That(f("Variable`1[[Demo.Game.Bot, Assembly-CSharp]]"), Is.EqualTo("Variable`1[[Demo.Units.Bot, Assembly-CSharp]]"));
    }

    [Test]
    public void NamespaceRenameEqualsTypeRenameForItsTypes()
    {
        foreach (var rel in TestData.UnityFiles(TestData.Project))
        {
            var text = TestData.Read(Path.Combine(TestData.Project, rel));
            var byNamespace = GraphRewriter.RewriteTypes(text, GraphRewriter.RenameNamespace("Demo.Game", "Demo.Bots"));
            var byTypes = GraphRewriter.RewriteTypes(text, GraphRewriter.Chain(new[] { "Bot", "Unit", "Pawn", "Relation", "HealAction", "Teleport" }
                .Select(t => GraphRewriter.RenameType("Demo.Game." + t, "Demo.Bots." + t))));
            Assert.That(byNamespace, Is.EqualTo(byTypes), rel);
        }
    }

    [Test]
    public void MarkerScannerFindsGraphsAcrossBufferBoundaries()
    {
        var text = new string('x', 100) + "_boundGraphSerialization: '{}'";
        for (var size = 8; size < 40; size++)
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));
            Assert.That(MarkerScanner.Contains(stream, size), Is.True, size.ToString());
        }

        using var empty = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 1000) + "_baseInfo"));
        Assert.That(MarkerScanner.Contains(empty, 16), Is.False);
    }
}
