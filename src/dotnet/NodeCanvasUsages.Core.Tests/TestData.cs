using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NodeCanvasUsages.Core.Tests;

internal static class TestData
{
    private static readonly Lazy<string> ourRoot = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "testData");
            if (Directory.Exists(Path.Combine(candidate, "project")))
                return candidate;
        }

        throw new DirectoryNotFoundException("testData/project not found above " + AppContext.BaseDirectory);
    });

    public static string Root => ourRoot.Value;
    public static string Project => Path.Combine(Root, "project");
    public static string Golden(string name) => Path.Combine(Root, "golden", name);

    public static readonly string[] GraphExtensions = { ".asset", ".prefab", ".unity" };

    /// <summary>
    /// Project-relative paths (forward slashes) of the Unity YAML files under <paramref name="root"/>/Assets — not the
    /// Library/ProjectSettings files Unity writes if the folder is opened in the editor.
    /// </summary>
    public static IEnumerable<string> UnityFiles(string root) =>
        (Directory.Exists(Path.Combine(root, "Assets"))
            ? Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*", SearchOption.AllDirectories)
            : Enumerable.Empty<string>())
            .Where(f => GraphExtensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>Reads text exactly as stored (UTF-8, no newline translation).</summary>
    public static string Read(string path) => File.ReadAllText(path, new System.Text.UTF8Encoding(false));
}
