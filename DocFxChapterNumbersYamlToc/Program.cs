using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace DocFxChapterNumbers
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                bool force = false;
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: {0} TOC.MD|TOC.YML TARGETDIRECTORY [--force]",
                        typeof(Program).Assembly.GetName().Name);
                    return 1;
                }

                if (args.Length > 2 && args[2].Equals("--force", StringComparison.OrdinalIgnoreCase))
                {
                    force = true;
                }

                string tocFile = Path.GetFullPath(args[0]);
                string sourceDirectory = Path.GetDirectoryName(tocFile)!;
                string targetDirectory = Path.GetFullPath(args[1]);

                if (!File.Exists(tocFile))
                {
                    Console.Error.WriteLine("Error: The specified TOC file '{0}' does not exist.", tocFile);
                    return 2;
                }

                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                else if (force)
                {
                    Console.WriteLine("Recreating existing target directory '{0}'.", targetDirectory);
                    Directory.Delete(targetDirectory, true);
                    Directory.CreateDirectory(targetDirectory);
                }

                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tocFile };
                string extension = Path.GetExtension(tocFile);

                if (".yml".Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Processing a YAML toc...");
                    ProcessYamlToc(sourceDirectory, tocFile, files, targetDirectory);
                }
                else if (".md".Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Processing a Markdown toc...");
                    ProcessMarkdownToc(sourceDirectory, files, targetDirectory, tocFile);
                }
                else
                {
                    Console.Error.WriteLine("Error: TOC file must have '.md' or '.yml' extension.");
                    return 1;
                }

                CopyAuxiliaryFiles(sourceDirectory, files, targetDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: {0}", ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return ex.HResult;
            }

            return 0;
        }

        private static void CopyAuxiliaryFiles(string sourceDirectory, HashSet<string> files, string targetDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories))
            {
                if (!files.Contains(file))
                {
                    string targetFile = GetOutputFullName(sourceDirectory, targetDirectory, file);
                    Console.WriteLine("Copying {0} -> {1}", Path.GetFileName(file), targetFile);

                    string directoryName = Path.GetDirectoryName(targetFile)!;
                    if (!Directory.Exists(directoryName))
                    {
                        Directory.CreateDirectory(directoryName);
                    }

                    File.Copy(file, targetFile, true);
                }
            }
        }

        private static void ProcessYamlToc(string sourceDirectory, string tocFile, HashSet<string> files, string targetDirectory)
        {
            var input = new StreamReader(tocFile);
            var yaml = new YamlStream();
            yaml.Load(input);

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            if (!root.Children.TryGetValue(new YamlScalarNode("items"), out var itemsNode))
            {
                Console.Error.WriteLine("Error: 'items' node not found in toc.yml.");
                return;
            }

            var chapter = new Chapter();

            ProcessYamlItemsRecursive((YamlSequenceNode)itemsNode, chapter, sourceDirectory, targetDirectory, files, 1);

            CopyAuxiliaryFiles(sourceDirectory, files, targetDirectory);

            // Write the modified YAML to the target directory
            var outputTocFile = Path.Combine(targetDirectory, Path.GetFileName(tocFile));
            using (var writer = new StreamWriter(outputTocFile))
            {
                yaml.Save(writer, assignAnchors: false);
            }
        }

        private static void ProcessYamlItemsRecursive(
            YamlSequenceNode itemsNode,
            Chapter chapter,
            string sourceDirectory,
            string targetDirectory,
            HashSet<string> files,
            int level)
        {
            foreach (YamlMappingNode item in itemsNode)
            {
                chapter.IncrementLevel(level); // TOC hierarchy level (Level 2+)

                // Update name
                if (item.Children.TryGetValue("name", out var nameNode) && nameNode is YamlScalarNode nameScalar)
                {
                    nameScalar.Value = $"{chapter} {nameScalar.Value}";
                }

                // Process href .md file
                if (item.Children.TryGetValue("href", out var hrefNode) && hrefNode is YamlScalarNode hrefScalar)
                {
                    var contentFile = Path.Combine(sourceDirectory, hrefScalar.Value).Replace('/', '\\');
                    files.Add(contentFile);
                    if (File.Exists(contentFile))
                    {
                        var targetFile = contentFile.Replace(sourceDirectory, targetDirectory);
                        string dummyHeading = null;
                        ProcessContentFile(new Chapter(chapter), contentFile, targetFile, ref dummyHeading);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: content file '{contentFile}' not found.");
                    }
                }

                // Recursively process nested items
                if (item.Children.TryGetValue("items", out var subItems) && subItems is YamlSequenceNode subSequence)
                {
                    ProcessYamlItemsRecursive(subSequence, new Chapter(chapter), sourceDirectory, targetDirectory, files, level + 1);
                }
            }
        }

        private static void ProcessMarkdownToc(string sourceDirectory, HashSet<string> files, string targetDirectory, string tocFile)
        {
            var doc = LoadMarkdownDocument(tocFile);

            var chapter = new Chapter();
            string? currentLevel1Heading = null;

            foreach (var x in doc.Descendants<HeadingBlock>())
            {
                if (x.Level == 1)
                    continue;

                chapter.IncrementLevel(x.Level - 1);

                if (x.Inline != null)
                {
                    foreach (var s in x.Inline)
                    {
                        if (s is LinkInline link)
                        {
                            if (link.FirstChild is LiteralInline name)
                            {
                                name.UpdateContent(c => chapter + " " + c);
                                if (!string.IsNullOrEmpty(link.Url))
                                {
                                    string contentFile = Path.Combine(sourceDirectory, link.Url).Replace('/', '\\');
                                    files.Add(contentFile);
                                    if (!File.Exists(contentFile))
                                    {
                                        Console.Error.WriteLine("Warning: Content file '{0}' does not exist.", contentFile);
                                    }
                                    else
                                    {
                                        string targetFile = GetOutputFullName(sourceDirectory, targetDirectory, contentFile);
                                        ProcessContentFile(new Chapter(chapter), contentFile, targetFile, ref currentLevel1Heading);
                                    }
                                }
                            }
                        }
                        else if (s is LiteralInline literal)
                        {
                            literal.UpdateContent(c => chapter + " " + c);
                            if (x.Level == 2)
                            {
                                currentLevel1Heading = literal.Content.ToString();
                            }
                        }
                    }
                }
            }

            Render(doc, GetOutputFullName(sourceDirectory, targetDirectory, tocFile));
        }

        private static string GetOutputFullName(string sourceDirectoryBase, string targetDirectoryBase, string fileName)
        {
            return fileName.Replace(sourceDirectoryBase, targetDirectoryBase);
        }

        private static MarkdownDocument LoadMarkdownDocument(string fileName)
        {
            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            return MarkdownParser.Parse(File.ReadAllText(fileName), pipeline);
        }

        private static void Render(MarkdownDocument doc, string fileName)
        {
            Console.WriteLine("Creating {0}", fileName);

            string directoryName = Path.GetDirectoryName(fileName)!;
            if (!Directory.Exists(directoryName))
            {
                Directory.CreateDirectory(directoryName);
            }

            var options = new NormalizeOptions();
            using (var writer = new StreamWriter(fileName))
            {
                var renderer = new NormalizeRenderer(writer, options);
                renderer.ObjectRenderers.Add(new NormalizeTableRenderer());
                var linkReferenceDefinitionRenderer = renderer.ObjectRenderers.Find<LinkReferenceDefinitionRenderer>();
                if (linkReferenceDefinitionRenderer != null)
                {
                    renderer.ObjectRenderers.Remove(linkReferenceDefinitionRenderer);
                }

                renderer.Render(doc);
            }
        }

        private static void ProcessContentFile(Chapter chapter, string contentFile, string targetFile, ref string? currentLevel1Heading)
        {
            var doc = LoadMarkdownDocument(contentFile);

            foreach (var x in doc.Descendants<HeadingBlock>())
            {
                if (x.Level > 1)
                {
                    chapter.IncrementLevel(x.Level + 1);
                }

                if (x.Inline != null)
                {
                    foreach (var s in x.Inline)
                    {
                        if (s is LiteralInline literal)
                        {
                            literal.UpdateContent(c => chapter + " " + c);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(currentLevel1Heading))
            {
                var heading = new HeadingBlock(new HeadingBlockParser())
                {
                    Level = 1,
                };

                var inline = new ContainerInline();
                inline.AppendChild(new LiteralInline(currentLevel1Heading));
                heading.Inline = inline;
                doc.Insert(0, heading);

                currentLevel1Heading = null;
            }

            Render(doc, targetFile);
        }
    }

    public static class MarkdownExtensions
    {
        public static LiteralInline UpdateContent(this LiteralInline literal, Func<string, string> update)
        {
            string current = literal.Content.ToString();
            current = update(current);
            literal.Content = new StringSlice(current);
            return literal;
        }
    }

    // This is from https://github.com/lunet-io/markdig/pull/184/files. As of Markdig 0.15.4
    // there is no support for Tables to roundtrip.
    public class NormalizeTableRenderer : NormalizeObjectRenderer<Table>
    {
        public const string PipeSeparator = "|";
        public const string HeaderSeparator = "---";
        public const string AlignmentChar = ":";
        public const string MarginSeparator = " ";
        protected override void Write(NormalizeRenderer renderer, Table obj)
        {
            renderer.EnsureLine();

            foreach (var row in obj.OfType<TableRow>())
            {
                renderer.Write(PipeSeparator);

                foreach (var tableCell in row)
                {
                    renderer.Write(MarginSeparator);

                    renderer.Render(tableCell);

                    renderer.Write(MarginSeparator);
                    renderer.Write(PipeSeparator);
                }

                renderer.WriteLine();


                if (row.IsHeader)
                {
                    bool alignmentEnabled = obj.ColumnDefinitions.Any(c => c.Alignment != TableColumnAlign.Left);

                    renderer.Write(PipeSeparator);

                    foreach (var column in obj.ColumnDefinitions)
                    {
                        renderer.Write(MarginSeparator);
                        if (alignmentEnabled && (column.Alignment == TableColumnAlign.Left || column.Alignment == TableColumnAlign.Center))
                        {
                            renderer.Write(AlignmentChar);
                        }
                        renderer.Write(HeaderSeparator);
                        if (alignmentEnabled && (column.Alignment == TableColumnAlign.Right || column.Alignment == TableColumnAlign.Center))
                        {
                            renderer.Write(AlignmentChar);
                        }
                        renderer.Write(MarginSeparator);
                        renderer.Write(PipeSeparator);
                    }

                    renderer.WriteLine();
                }
            }

            renderer.FinishBlock(true);
        }
    }

    public class YamlTocItem
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }
        [YamlMember(Alias = "href")]
        public string? HRef { get; set; }
        [YamlMember(Alias = "items")]
        public List<YamlTocItem> Items { get; set; } = [];
    }

    public class Chapter
    {
        private int m_level1;
        private int m_level2;
        private int m_level3;
        private int m_level4;
        private int m_level5;
        private int m_level6;

        public Chapter()
        {
        }

        public Chapter(Chapter other)
        {
            m_level1 = other.m_level1;
            m_level2 = other.m_level2;
            m_level3 = other.m_level3;
            m_level4 = other.m_level4;
            m_level5 = other.m_level5;
            m_level6 = other.m_level6;
        }

        public int Level
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

        public void IncrementLevel(int level)
        {
            switch (level)
            {
                case 1:
                    IncrementLevel1();
                    break;
                case 2:
                    IncrementLevel2();
                    break;
                case 3:
                    IncrementLevel3();
                    break;
                case 4:
                    IncrementLevel4();
                    break;
                case 5:
                    IncrementLevel5();
                    break;
                case 6:
                    IncrementLevel6();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, "Level out of range.");
            }
        }

        public void IncrementLevel1()
        {
            m_level1++;
            m_level2 = m_level3 = m_level4 = m_level5 = m_level6 = 0;
        }

        public void IncrementLevel2()
        {
            m_level2++;
            m_level3 = m_level4 = m_level5 = m_level6 = 0;
        }

        public void IncrementLevel3()
        {
            m_level3++;
            m_level4 = m_level5 = m_level6 = 0;
        }

        public void IncrementLevel4()
        {
            m_level4++;
            m_level5 = m_level6 = 0;
        }

        public void IncrementLevel5()
        {
            m_level5++;
            m_level6 = 0;
        }

        public void IncrementLevel6()
        {
            m_level6++;
        }

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
}