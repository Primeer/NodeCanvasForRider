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
        // libyaml never breaks inside a run of spaces: a line ending in a space would lose it when read back
        var doubleSpace = new string('a', 74) + "  b";
        Assert.That(YamlScalar.EncodeSingle("k", 2, doubleSpace), Is.EqualTo(new[] { "  k: '" + doubleSpace + "'" }));
        var longRun = new string('a', 74) + "   " + new string('b', 10) + " c";
        Assert.That(YamlScalar.EncodeSingle("k", 2, longRun), Is.EqualTo(new[] { "  k: '" + new string('a', 74) + "   " + new string('b', 10), "    c'" }));
        var trailing = new string('a', 74) + " ";
        Assert.That(YamlScalar.EncodeSingle("k", 2, trailing), Is.EqualTo(new[] { "  k: '" + trailing + "'" }));
    }

    [TestCase("it's")]
    [TestCase("line1\nline2")]
    [TestCase("a\n\nb")]
    [TestCase("ends with break\n")]
    [TestCase("trailing spaces kept   ")]
    [TestCase("two  spaces at the fold column: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  b")]
    [TestCase("a\n")]
    [TestCase("\na")]
    [TestCase("a \n b")]
    [TestCase("")]
    public void RoundTripsSpecialValues(string value)
    {
        var lines = YamlScalar.Encode("_serializedGraph", 2, value);
        var span = YamlScalar.Find(lines, "_serializedGraph");
        Assert.That(span, Is.Not.Null);
        Assert.That(span!.Value, Is.EqualTo(value));
    }

    [Test]
    public void TrailingBlanksBeforeAFoldAreDropped()
    {
        // YAML (and Unity's parser) drops blanks around a line fold; blanks before the closing quote are content
        var lines = new[] { "  k: 'one ", "    two  ", "    three  '" };
        Assert.That(YamlScalar.Find(lines, "k")!.Value, Is.EqualTo("one two three  "));
    }

    [TestCase(new[] { "  k: 'a", "", "    '" }, "a\n")]
    [TestCase(new[] { "  k: 'a", "    '" }, "a ")]
    [TestCase(new[] { "  k: 'a", "", "", "    '" }, "a\n\n")]
    [TestCase(new[] { "  k: '", "    a'" }, " a")]
    public void SingleQuotedFoldingAtTheClosingQuote(string[] lines, string expected) =>
        Assert.That(YamlScalar.Find(lines, "k")!.Value, Is.EqualTo(expected));

    [Test]
    public void ValueEndingInBreakStaysSingleQuoted()
    {
        var lines = YamlScalar.Encode("k", 2, "a\n");
        Assert.That(lines, Is.EqualTo(new[] { "  k: 'a", "", "    '" }));
    }

    [TestCase("  k: \"a\\eb\\_c\\N\"", "a\u001bb\u00a0c\u0085")]
    [TestCase("  k: \"\\a\\v\\0\\L\\P\\/\\ \"", "\a\v\0\u2028\u2029/ ")]
    [TestCase("  k: \"\\x41\\u0042\\U0001F600\"", "AB\U0001F600")]
    public void DoubleQuotedEscapes(string line, string expected) =>
        Assert.That(YamlScalar.Find(new[] { line }, "k")!.Value, Is.EqualTo(expected));

    [TestCase(new[] { "  k: \"a", "", "    b\"" }, "a\nb")]
    [TestCase(new[] { "  k: \"a  ", "    b\"" }, "a b")]
    [TestCase(new[] { "  k: \"a\\", "    b\"" }, "ab")]
    [TestCase(new[] { "  k: \"a\\\\", "    b\"" }, "a\\ b")]
    [TestCase(new[] { "  k: \"a\\ ", "    b\"" }, "a  b")]
    public void DoubleQuotedFolding(string[] lines, string expected) =>
        Assert.That(YamlScalar.Find(lines, "k")!.Value, Is.EqualTo(expected));

    [TestCase("  k: \"\\u12\"")]
    [TestCase("  k: \"\\x4\"")]
    [TestCase("  k: \"\\uZZZZ\"")]
    [TestCase("  k: \"\\q\"")]
    public void MalformedDoubleQuotedEscapeIsAFormatError(string line) =>
        Assert.That(() => YamlScalar.Find(new[] { line }, "k"), Throws.TypeOf<System.FormatException>());

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
