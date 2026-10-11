using System;
using System.IO;
using System.Linq;
using System.Text;

namespace NodeCanvasUsages.Core.Model;

/// <summary>
/// Cheap pre-filter: does a file contain a serialized graph at all? Scans raw bytes in blocks, so large scenes
/// without graphs are never decoded.
/// </summary>
public static class MarkerScanner
{
    /// <summary>Keys of serialized graphs in assets and in GraphOwner components.</summary>
    public static readonly string[] GraphMarkers = { "_serializedGraph", "_boundGraphSerialization" };

    private static readonly byte[][] ourMarkers = GraphMarkers.Select(Encoding.ASCII.GetBytes).ToArray();

    public static bool ContainsGraph(string text) =>
        GraphMarkers.Any(m => text.IndexOf(m, StringComparison.Ordinal) >= 0);

    public static bool Contains(Stream stream, int bufferSize = 64 * 1024)
    {
        var overlap = ourMarkers.Max(m => m.Length) - 1;
        var buffer = new byte[bufferSize + overlap];
        var kept = 0;
        while (true)
        {
            var read = stream.Read(buffer, kept, bufferSize);
            if (read <= 0)
                return false;
            var length = kept + read;
            if (ourMarkers.Any(m => IndexOf(buffer, length, m) >= 0))
                return true;
            kept = length < overlap ? length : overlap;
            System.Array.Copy(buffer, length - kept, buffer, 0, kept);
        }
    }

    public static bool ContainsFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.SequentialScan);
        return Contains(stream);
    }

    private static int IndexOf(byte[] buffer, int length, byte[] marker)
    {
        var first = marker[0];
        for (var i = 0; i <= length - marker.Length; i++)
        {
            if (buffer[i] != first)
                continue;
            var k = 1;
            while (k < marker.Length && buffer[i + k] == marker[k])
                k++;
            if (k == marker.Length)
                return i;
        }

        return -1;
    }
}
