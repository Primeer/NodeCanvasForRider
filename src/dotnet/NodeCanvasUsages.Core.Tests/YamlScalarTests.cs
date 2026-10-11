using System.Collections.Generic;
using System.IO;
using System.Linq;
using NodeCanvasUsages.Core.Model;
using NodeCanvasUsages.Core.Yaml;
using NUnit.Framework;

namespace NodeCanvasUsages.Core.Tests;

[TestFixture]
public class YamlScalarTests
{
    public static IEnumerable<string> AllFiles() =>
        TestData.UnityFiles(TestData.Project).Select(f => Path.Combine(TestData.Project, f))
            .Concat(Directory.EnumerateDirectories(Path.Combine(TestData.Root, "golden"))
                .SelectMany(g => TestData.UnityFiles(g).Select(f => Path.Combine(g, f))))
            .Select(f => Path.GetRelativePath(TestData.Root, f).Replace('\\', '/'));

    [TestCaseSource(nameof(AllFiles))]
    public void DecodeEncodeIsByteIdentical(string relative)
    {
        var text = TestData.Read(Path.Combine(TestData.Root, relative));
        var file = new UnityYamlFile(text);
        Assert.That(file.Slots, Is.Not.Empty, "no graph found");
        foreach (var slot in file.Slots)
        {
            var span = file.ReadScalar(slot);
            var (start, end) = file.Range(span);
            Assert.That(file.EncodeScalar(span, span.Value), Is.EqualTo(text.Substring(start, end - start)),
                $"{slot.GraphKey} in {relative}");
        }

        Assert.That(GraphRewriter.Rewrite(text, s => s), Is.Null, "identity rewrite must not touch the file");
    }

    [Test]
    public void FoldsAtColumnEightyInclusive()
    {
        // "  k: '" is 6 columns; a space at column 80 (0-based) is folded, one at 79 is not
        var value = new string('a', 74) + " b";
        Assert.That(YamlScalar.EncodeSingle("k", 2, value), Is.EqualTo(new[] { "  k: '" + new string('a', 74), "    b'" }));
        var shorter = new string('a', 73) + " b";
        Assert.That(YamlScalar.EncodeSingle("k", 2, shorter), Is.EqualTo(new[] { "  k: '" + shorter + "'" }));
    }

    [Test]
    public void DoesNotFoldBeforeSpaceOrAtEnds()
    {
        var doubleSpace = new string('a', 74) + "  b";
        Assert.That(YamlScalar.EncodeSingle("k", 2, doubleSpace)[0], Is.EqualTo("  k: '" + new string('a', 74) + " "));
        var trailing = new string('a', 74) + " ";
        Assert.That(YamlScalar.EncodeSingle("k", 2, trailing), Is.EqualTo(new[] { "  k: '" + trailing + "'" }));
    }

    [TestCase("it's")]
    [TestCase("line1\nline2")]
    [TestCase("a\n\nb")]
    [TestCase("ends with break\n")]
    [TestCase("trailing spaces kept   ")]
    [TestCase("")]
    public void RoundTripsSpecialValues(string value)
    {
        var lines = YamlScalar.Encode("_serializedGraph", 2, value);
        var span = YamlScalar.Find(lines, "_serializedGraph");
        Assert.That(span, Is.Not.Null);
        Assert.That(span!.Value, Is.EqualTo(value));
    }

    [Test]
    public void ContinuationLinesKeepTrailingSpaces()
    {
        var lines = new[] { "  k: 'one", "    two  ", "    three'" };
        Assert.That(YamlScalar.Find(lines, "k")!.Value, Is.EqualTo("one two   three"));
    }

    [Test]
    public void EscapedQuoteIsDecoded()
    {
        var lines = new[] { "  k: '{\"a\":\"it''s\"}'" };
        Assert.That(YamlScalar.Find(lines, "k")!.Value, Is.EqualTo("{\"a\":\"it's\"}"));
    }

    [Test]
    public void MapPointsIntoTheFile()
    {
        var text = "x: 1\n  k: 'abc\n    def'\n";
        var file = new UnityYamlFile(text);
        var span = YamlScalar.Find(file.Lines, "k", lineOffsets: file.LineOffsets)!;
        Assert.That(span.Value, Is.EqualTo("abc def"));
        Assert.That(text[span.Map[0]], Is.EqualTo('a'));
        Assert.That(text[span.Map[4]], Is.EqualTo('d'));
    }
}
