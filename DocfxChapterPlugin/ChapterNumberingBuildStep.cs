using Docfx.Common;
using Docfx.Plugins;
using HtmlAgilityPack;
using System.Collections.Immutable;
using System.Composition;
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
            ProcessTocFiles(manifest, outputFolder, cancellationToken);
        }

        return manifest;
    }

    private void ProcessTocFiles(Manifest manifest, string outputFolder, CancellationToken cancellationToken)
    {
        if (manifest.Files == null)
        {
            return;
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Type != "Toc" || !file.Output.TryGetValue(".html", out var outputFileInfo))
            {
                continue;
            }

            string? match = m_chapterNumbersOn.FirstOrDefault(on => outputFileInfo.RelativePath
                .StartsWith(on, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                continue;
            }

            string filePath = Path.Combine(outputFolder, outputFileInfo.RelativePath);

            Logger.LogInfo($"{LogPrefix}: Processing {filePath}");

            var html = LoadFile(filePath);
            if (html == null)
            {
                continue;
            }

            // Regex to match "level" followed by a number
            var levelRegex = new Regex(@"^level\d+$");

            var rootNodes = html.DocumentNode.Descendants("ul").Where(node => GetNavLevel(node) == 1).ToList();
            if (rootNodes.Count > 0)
            {
                if (rootNodes.Count > 1)
                {
                    Logger.LogWarning($"{LogPrefix}: Found {rootNodes.Count} root nodes, but only 1 was expected. Ignored all but the first");
                }

                var chapter = new Chapter();
                var rootNode = rootNodes[0];
                ProcessUL(Path.Combine(outputFolder, match), ref chapter, rootNode, level: 1, cancellationToken);

                html.Save(filePath);
            }
            else
            {
                Logger.LogWarning($"{LogPrefix}: Found no root nodes in '{outputFileInfo.RelativePath}'");
            }
        }
    }

    private void ProcessUL(string outputFolder, ref Chapter chapter, HtmlNode ul, int level, CancellationToken cancellationToken)
    {
        foreach (var li in ul.Elements("li").ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProcessLI(outputFolder, ref chapter, level, li, cancellationToken);
        }
    }

    private void ProcessLI(string outputFolder, ref Chapter chapter, int level, HtmlNode li, CancellationToken cancellationToken)
    {
        chapter = chapter.IncrementLevel(level);

        // Find the first <a> directly under <li> (not deeper)
        var a = li.Elements("a").FirstOrDefault();
        if (a != null)
        {
            string? href = a.GetAttributeValue("href", null);
            a.InnerHtml = $"{chapter}\u2003{a.InnerText}";
            string? title = a.GetAttributeValue("title", null);
            if (title != null)
            {
                a.SetAttributeValue("title", $"{chapter} {title}");
            }

            if (href != null)
            {
                // Process content file
                var ch = chapter;
                ProcessContentFile(ref ch, Path.Combine(outputFolder, href.Replace('/', '\\')), cancellationToken);
            }
        }

        // Find nested <ul> inside this <li> (for children)
        var childUl = li.Elements("ul").FirstOrDefault();
        if (childUl != null)
        {
            int childLevel = GetNavLevel(childUl);
            if (childLevel != 0)
            {
                ProcessUL(outputFolder, ref chapter, childUl, childLevel, cancellationToken);
            }
        }
    }

    private void ProcessContentFile(ref Chapter chapter, string contentFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var html = LoadFile(contentFile);
        if (html == null)
        {
            Logger.LogWarning($"{LogPrefix}: content file '{contentFile}' not found");
            return;
        }

        int count = TraverseHeadings(ref chapter, html.DocumentNode);
        Logger.LogInfo($"{LogPrefix}: updated {count} headings in {contentFile}");
        if (count > 0)
        {
            html.Save(contentFile);
        }
    }

    public static int TraverseHeadings(ref Chapter chapter, HtmlNode root)
    {
        return Traverse(ref chapter, root);

        static int Traverse(ref Chapter chapter, HtmlNode node)
        {
            int res = 0;

            if (node.NodeType == HtmlNodeType.Element)
            {
                string name = node.Name.ToLowerInvariant();
                if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]))
                {
                    int level = name[1] - '0';
                    if (level >= 1 && level <= 6)
                    {
                        if (node.GetAttributeValue("id", null) != null &&
                            !node.GetAttributeValue("class", "")
                                .Contains("offcanvas-", StringComparison.OrdinalIgnoreCase))
                        {
                            if (level > 1)
                            {
                                chapter = chapter.IncrementLevel(level + 1);
                            }
                            node.InnerHtml = $"{chapter}\u2003{node.InnerText}";
                            res++;
                        }
                    }
                }
            }

            // Recursively check children
            foreach (var child in node.ChildNodes)
            {
                res += Traverse(ref chapter, child);
            }

            return res;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------


    private int GetNavLevel(HtmlNode ul)
    {
        ReadOnlySpan<char> classAttr = ul.GetAttributeValue("class", "").AsSpan();

        bool hasNav = false;
        int level = 0;

        while (!classAttr.IsEmpty)
        {
            // Trim leading whitespace
            int spaceIndex = classAttr.IndexOf(' ');
            ReadOnlySpan<char> token;

            if (spaceIndex >= 0)
            {
                token = classAttr.Slice(0, spaceIndex);
                classAttr = classAttr.Slice(spaceIndex + 1);
            }
            else
            {
                token = classAttr;
                classAttr = ReadOnlySpan<char>.Empty;
            }

            if (token.SequenceEqual("nav"))
            {
                hasNav = true;
            }
            else if (token.StartsWith("level".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                     token.Length > 5 &&
                     int.TryParse(token.Slice(5), out int parsedLevel))
            {
                level = parsedLevel;
            }
        }

        return hasNav ? level : 0;
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
