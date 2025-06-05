#if false
using Docfx.Common;
using Docfx.Plugins;
using HtmlAgilityPack;
using Spectre.Console;
using System.Collections;
using System.Collections.Immutable;
using System.Composition;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;

namespace DocfxChapterPlugin;

[Export(nameof(ChapterNumbers), typeof(IPostProcessor))]
public class ChapterNumbers : IPostProcessor
{
    private const string LogPrefix = nameof(ChapterNumbers);
    private readonly HashSet<string> m_chapterNumbersOn = new(StringComparer.OrdinalIgnoreCase);

    public ImmutableDictionary<string, object> PrepareMetadata(ImmutableDictionary<string, object> metadata)
    {
        if (metadata.TryGetValue("chapterNumbersOn", out var value) && value is List<object> on)
        {
            foreach (var entry in on)
            {
                string? str = entry?.ToString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    m_chapterNumbersOn.Add(str);
                }
            }
        }

        if (m_chapterNumbersOn.Count == 0)
        {
            Logger.LogWarning($"{LogPrefix}: No relative paths specified for chapter numbers. Check 'chapterNumbersOn' meta data in docfx.json");
        }
        else
        {
            Logger.LogInfo($"{LogPrefix}: considering paths {string.Join(", ", m_chapterNumbersOn)}");
        }

        return metadata;
    }

    public Manifest Process(Manifest manifest, string outputFolder, CancellationToken cancellationToken)
    {
        if (m_chapterNumbersOn.Count > 0)
        {
            var tocInfos = HandleToc(manifest, outputFolder, cancellationToken);
            if (tocInfos.Count == 0)
            {
                return manifest;
            }

            HandleContentFiles(manifest, outputFolder, tocInfos, cancellationToken);
        }

        return manifest;
    }

    private Dictionary<string, TocEntry> HandleToc(Manifest manifest, string outputFolder, CancellationToken cancellationToken)
    {
        var tocFiles = (
            from item in manifest.Files ?? Enumerable.Empty<ManifestItem>()
            from output in item.Output
            where item.Type == "Toc" && output.Key.Equals(".html", StringComparison.OrdinalIgnoreCase)
            from match in m_chapterNumbersOn
            where output.Value.RelativePath.StartsWith(match, StringComparison.OrdinalIgnoreCase)
            select (output.Value.RelativePath, item.Metadata, Match: match)).ToList();

        Logger.LogInfo($"{LogPrefix}: Inspecting {tocFiles.Count} html TOC files");

        var tocInfos = new Dictionary<string, TocEntry>(StringComparer.OrdinalIgnoreCase);

        if (tocFiles.Count == 0)
        {
            return tocInfos;
        }

        foreach ((string relativePath, Dictionary<string, object> metadata, string match) in tocFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string filePath = Path.Combine(outputFolder, relativePath);
            var html = LoadFile(filePath);
            if (html == null)
            {
                continue;
            }

            // Regex to match "level" followed by a number
            var levelRegex = new Regex(@"^level\d+$");
            var tocEntries = new List<TocEntry>();

            var rootNodes = html.DocumentNode.Descendants("ul").Where(node => GetNavLevel(node) == 1).ToList();
            if (rootNodes.Count != 0)
            {
                if (rootNodes.Count > 1)
                {
                    Logger.LogWarning($"{LogPrefix}: Found {rootNodes.Count} root nodes, but only 1 was expected. Ignored all but the first");
                }

                var entry = HandleRoot(match, rootNodes[0], cancellationToken);

                //if (Logger.LogLevelThreshold <= LogLevel.Diagnostic)
                //{
                PrintTocEntries(LogLevel.Info, entry);
                //}

                tocInfos[match] = entry;

                html.Save(filePath);
            }
            else
            {
                Logger.LogWarning($"{LogPrefix}: Found no toc root nodes in '{relativePath}'");
            }
        }

        return tocInfos;
    }

    private TocEntry HandleRoot(string match, HtmlNode ul, CancellationToken cancellationToken)
    {
        var chapter = new Chapter();
        var root = new TocEntry { Level = 0, Chapter = chapter };
        root.Items = HandleTocSection(chapter, match, ul, 1, cancellationToken);
        return root;
    }

    private List<TocEntry> HandleTocSection(Chapter chapter, string match, HtmlNode ul, int level, CancellationToken cancellationToken)
    {
        var entries = new List<TocEntry>();

        foreach (var li in ul.Elements("li").ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            chapter = chapter.IncrementLevel(level);

            var entry = new TocEntry { Level = level, Chapter = chapter };

            // Find the first <a> directly under <li> (not deeper)
            var a = li.Elements("a").FirstOrDefault();
            if (a != null)
            {
                entry.Name = a.InnerText.Trim();
                entry.HRef = a.GetAttributeValue("href", null);
                a.InnerHtml = $"{chapter} {a.InnerText}";
                string? title = a.GetAttributeValue("title", null);
                if (title != null)
                {
                    a.SetAttributeValue("title", $"{chapter} {title}");
                }
            }

            // Find nested <ul> inside this <li> (for children)
            var childUl = li.Elements("ul").FirstOrDefault();
            if (childUl != null)
            {
                int childLevel = GetNavLevel(childUl);
                if (childLevel != 0)
                {
                    entry.Items = HandleTocSection(chapter, match, childUl, childLevel, cancellationToken);
                }
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static TocEntry? FindTocEntryByHref(TocEntry tocEntry, string href)
    {
        if (string.Equals(tocEntry.HRef, href, StringComparison.OrdinalIgnoreCase))
            return tocEntry;

        return FindTocEntryByHref(tocEntry.Items, href);
    }

    private static TocEntry? FindTocEntryByHref(List<TocEntry> tocEntries, string href)
    {
        if (tocEntries == null || string.IsNullOrEmpty(href))
            return null;

        foreach (var entry in tocEntries)
        {
            if (string.Equals(entry.HRef, href, StringComparison.OrdinalIgnoreCase))
                return entry;

            var foundInChildren = FindTocEntryByHref(entry.Items, href);
            if (foundInChildren != null)
                return foundInChildren;
        }

        return null;
    }

    // ----------------------------------------------------------------------------------------

    private void HandleContentFiles(Manifest manifest, string outputFolder, Dictionary<string, TocEntry> tocInfos, CancellationToken cancellationToken)
    {
        var contentFiles = (
            from item in manifest.Files ?? Enumerable.Empty<ManifestItem>()
            from output in item.Output
            where item.Type == "Conceptual" && output.Key.Equals(".html", StringComparison.OrdinalIgnoreCase)
            from match in m_chapterNumbersOn
            where output.Value.RelativePath.StartsWith(match, StringComparison.OrdinalIgnoreCase)
            select (output.Value.RelativePath, item.Metadata, Match: match)).ToList();

        if (contentFiles.Count == 0)
        {
            Logger.LogWarning($"{LogPrefix}: No matching content files found.");
            return;
        }

        Logger.LogInfo($"{LogPrefix}: Adding chapter numbers to {contentFiles.Count} html TOC files");
        foreach ((string relativePath, Dictionary<string, object> metadata, string match) in contentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!tocInfos.TryGetValue(match, out var rootTocEntry))
            {
                Logger.LogWarning($"{LogPrefix}: no toc entries found for '{match}' (processing '{relativePath}')");
                continue;
            }

            string href = StripFirstPathSegmentIfMatches(relativePath, match);

            var tocEntry = FindTocEntryByHref(rootTocEntry, href);
            if (tocEntry == null)
            {
                Logger.LogWarning($"{LogPrefix}: no toc entry found for '{relativePath}'.");
            }
            else
            {
                Logger.LogInfo($"{LogPrefix}: " + tocEntry.Name + " /" + tocEntry.Level + " /" + tocEntry.HRef);

                string filePath = Path.Combine(outputFolder, relativePath);
                var html = LoadFile(filePath);
                if (html == null)
                {
                    continue;
                }

                var chapter = tocEntry.Chapter;

                TraverseHeadings(ref chapter, html.DocumentNode);

                html.Save(filePath);
            }
        }
    }

    public static void TraverseHeadings(ref Chapter chapter, HtmlNode root)
    {
        Traverse(ref chapter, root);

        static void Traverse(ref Chapter chapter, HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Element)
            {
                string name = node.Name.ToLowerInvariant();
                if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]))
                {
                    int level = name[1] - '0' + 1;
                    if (level >= 1 && level <= 6)
                    {
                        if (node.GetAttributeValue("id", null) != null &&
                            !node.GetAttributeValue("class", "")
                                .Contains("offcanvas-", StringComparison.OrdinalIgnoreCase))
                        {
                            chapter = chapter.IncrementLevel(level);
                            node.InnerHtml = $"{chapter} {node.InnerText}";
                        }
                    }
                }
            }

            // Recursively check children
            foreach (var child in node.ChildNodes)
            {
                Traverse(ref chapter, child);
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------


    private class TocEntry
    {
        public string? Name { get; set; }
        public string? HRef { get; set; }
        public int Level { get; init; }
        public Chapter Chapter { get; init; }
        public List<TocEntry> Items { get; set; } = new List<TocEntry>();
    }

    private int GetNavLevel(HtmlNode ul)
    {
        var classAttr = ul.GetAttributeValue("class", "");
        var classes = classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (!classes.Contains("nav"))
            return 0;

        var levelClass = classes.FirstOrDefault(c => c.StartsWith("level"));
        if (levelClass != null && int.TryParse(levelClass.Substring("level".Length), out int level))
        {
            return level;
        }

        return 0; // Not a valid nav level class
    }

    private static void PrintTocEntries(LogLevel level, TocEntry entry)
    {
        PrintTocEntries(level, entry.Items);
    }

    private static void PrintTocEntries(LogLevel level, List<TocEntry> entries, int indent = 0)
    {
        foreach (var entry in entries)
        {
            string indentString = new string(' ', indent * 2); // 2 spaces per level
            Logger.Log(level, $"{LogPrefix}: {indentString}- {entry.Chapter} [{entry.Name}]({entry.HRef}) (Level: {entry.Level})");

            // Recurse into child items
            if (entry.Items != null && entry.Items.Count > 0)
            {
                PrintTocEntries(level, entry.Items, indent + 1);
            }
        }
    }

    private static HtmlDocument? LoadFile(string filePath)
    {
        HtmlDocument? html = null;
        try
        {
            if (EnvironmentContext.FileAbstractLayer.Exists(filePath))
            {
                using var stream = EnvironmentContext.FileAbstractLayer.OpenRead(filePath);
                html = new HtmlDocument();
                html.Load(stream, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"{LogPrefix}: Can't load content from {filePath}: {ex.Message}");
        }

        return html;
    }

    public static string StripFirstPathSegmentIfMatches(string path, string segmentToStrip)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(segmentToStrip))
            return path;

        ReadOnlySpan<char> span = path;
        ReadOnlySpan<char> segmentSpan = segmentToStrip;

        // Find the first separator index
        int sepIndex = span.IndexOfAny('/', '\\');

        ReadOnlySpan<char> firstSegment;

        if (sepIndex == -1)
        {
            // No separator, entire path is one segment
            firstSegment = span;
            // If it matches segmentToStrip, strip whole path (return empty)
            return firstSegment.SequenceEqual(segmentSpan) ? string.Empty : path;
        }
        else
        {
            // Slice first segment up to separator
            firstSegment = span.Slice(0, sepIndex);
        }

        // Compare first segment with segmentToStrip
        if (!firstSegment.SequenceEqual(segmentSpan))
        {
            // No match, return original path
            return path;
        }

        // Match! Now skip the first segment + separator(s)
        int startIndex = sepIndex + 1;

        // Skip any additional separators after first (e.g., foo//bar)
        while (startIndex < span.Length && (span[startIndex] == '/' || span[startIndex] == '\\'))
            startIndex++;

        if (startIndex >= span.Length)
        {
            // Nothing remains after first segment
            return string.Empty;
        }

        return span.Slice(startIndex).ToString();
    }
}

public readonly struct Chapter
{
    private readonly int m_level1;
    private readonly int m_level2;
    private readonly int m_level3;
    private readonly int m_level4;
    private readonly int m_level5;
    private readonly int m_level6;

    public Chapter()
    {
    }

    public Chapter(Chapter other)
        : this(other.m_level1, other.m_level2, other.m_level3, other.m_level4, other.m_level5, other.m_level6)
    {
    }

    public Chapter(int level1, int level2, int level3, int level4, int level5, int level6)
    {
        m_level1 = level1;
        m_level2 = level2;
        m_level3 = level3;
        m_level4 = level4;
        m_level5 = level5;
        m_level6 = level6;
    }

    public int Depth
    {
        get
        {
            if (m_level6 > 0) return 6;
            if (m_level5 > 0) return 5;
            if (m_level4 > 0) return 4;
            if (m_level3 > 0) return 3;
            if (m_level2 > 0) return 2;
            if (m_level1 > 0) return 1;
            return 0;
        }
    }

    public Chapter IncrementLevel(int level) => level switch
    {
        1 => IncrementLevel1(),
        2 => IncrementLevel2(),
        3 => IncrementLevel3(),
        4 => IncrementLevel4(),
        5 => IncrementLevel5(),
        6 => IncrementLevel6(),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Level out of range."),
    };

    public Chapter IncrementLevel1() => new(m_level1 + 1, 0, 0, 0, 0, 0);
    public Chapter IncrementLevel2() => new(m_level1, m_level2 + 1, 0, 0, 0, 0);
    public Chapter IncrementLevel3() => new(m_level1, m_level2, m_level3 + 1, 0, 0, 0);
    public Chapter IncrementLevel4() => new(m_level1, m_level2, m_level3, m_level4 + 1, 0, 0);
    public Chapter IncrementLevel5() => new(m_level1, m_level2, m_level3, m_level4, m_level5 + 1, 0);
    public Chapter IncrementLevel6() => new(m_level1, m_level2, m_level3, m_level4, m_level5, m_level6 + 1);

    public override string ToString()
    {
        var sb = new StringBuilder();

        if (m_level1 > 0)
        {
            sb.Append(m_level1);

            if (m_level2 > 0)
            {
                sb.Append('.');
                sb.Append(m_level2);

                if (m_level3 > 0)
                {
                    sb.Append('.');
                    sb.Append(m_level3);

                    if (m_level4 > 0)
                    {
                        sb.Append('.');
                        sb.Append(m_level4);

                        if (m_level5 > 0)
                        {
                            sb.Append('.');
                            sb.Append(m_level5);

                            if (m_level6 > 0)
                            {
                                sb.Append('.');
                                sb.Append(m_level6);
                            }
                        }
                    }
                }
            }
        }

        return sb.ToString();
    }
}
#endif