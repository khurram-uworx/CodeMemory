using CodeMemory.Indexing;
using CodeMemory.Indexing.Configuration;
using CodeMemory.Indexing.Parsing;

namespace CodeMemory.Tests.Indexing.Configuration;

public sealed class CodeMemoryConfigTests
{
    [Test]
    public void Load_WhenNoFile_ReturnsDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var config = CodeMemoryConfig.Load(dir);
            Assert.That(config, Is.SameAs(CodeMemoryConfig.Default));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Load_WhenFileExists_ParsesCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".codememory.json"), """
                {
                    "exclude": ["vendor/", "*.generated.cs"],
                    "languageOverrides": {
                        ".ts": "JavaScript",
                        ".cxx": "Cpp"
                    },
                    "clusteringThreshold": 0.5
                }
                """);

            var config = CodeMemoryConfig.Load(dir);
            Assert.That(config.Exclude, Has.Count.EqualTo(2));
            Assert.That(config.Exclude, Does.Contain("vendor/"));
            Assert.That(config.Exclude, Does.Contain("*.generated.cs"));
            Assert.That(config.LanguageOverrides, Has.Count.EqualTo(2));
            Assert.That(config.LanguageOverrides[".ts"], Is.EqualTo("JavaScript"));
            Assert.That(config.LanguageOverrides[".cxx"], Is.EqualTo("Cpp"));
            Assert.That(config.ClusteringThreshold, Is.EqualTo(0.5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Load_WhenFileHasInvalidJson_ReturnsDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".codememory.json"), "not valid json");
            var config = CodeMemoryConfig.Load(dir);
            Assert.That(config, Is.SameAs(CodeMemoryConfig.Default));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Load_WhenFileIsEmpty_ReturnsDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".codememory.json"), "");
            var config = CodeMemoryConfig.Load(dir);
            Assert.That(config, Is.SameAs(CodeMemoryConfig.Default));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ResolvedLanguageOverrides_WithValidEntries_ReturnsMappedDictionary()
    {
        var config = new CodeMemoryConfig
        {
            LanguageOverrides = new Dictionary<string, string>
            {
                [".ts"] = "JavaScript",
                [".jsx"] = "TypeScript",
            }
        };

        var resolved = config.ResolvedLanguageOverrides();
        Assert.That(resolved, Has.Count.EqualTo(2));
        Assert.That(resolved[".ts"], Is.EqualTo(Language.JavaScript));
        Assert.That(resolved[".jsx"], Is.EqualTo(Language.TypeScript));
    }

    [Test]
    public void ResolvedLanguageOverrides_WithMissingDot_AddsDotPrefix()
    {
        var config = new CodeMemoryConfig
        {
            LanguageOverrides = new Dictionary<string, string>
            {
                ["ts"] = "JavaScript",
            }
        };

        var resolved = config.ResolvedLanguageOverrides();
        Assert.That(resolved, Has.Count.EqualTo(1));
        Assert.That(resolved[".ts"], Is.EqualTo(Language.JavaScript));
    }

    [Test]
    public void ResolvedLanguageOverrides_WithInvalidLanguage_LogsWarningAndSkips()
    {
        var config = new CodeMemoryConfig
        {
            LanguageOverrides = new Dictionary<string, string>
            {
                [".xyz"] = "NonExistentLanguage",
            }
        };

        var resolved = config.ResolvedLanguageOverrides();
        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public void ResolvedLanguageOverrides_WithEmptyConfig_ReturnsEmpty()
    {
        var config = new CodeMemoryConfig();
        var resolved = config.ResolvedLanguageOverrides();
        Assert.That(resolved, Is.Empty);
    }

    [Test]
    public async Task FileCrawler_WithAdditionalExclusions_ExcludesMatchingFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "keep.cs"), "// keep");
            File.WriteAllText(Path.Combine(dir, "ignore.generated.cs"), "// ignore");
            Directory.CreateDirectory(Path.Combine(dir, "vendor"));
            File.WriteAllText(Path.Combine(dir, "vendor", "lib.cs"), "// vendor lib");

            var crawler = new FileCrawler(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCrawler>.Instance);

            var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "vendor/",
                "*.generated.cs"
            };

            var files = await crawler.WalkAsync(dir, additionalExclusions: exclusions)
                .Select(f => f.RelativePath.Replace('\\', '/'))
                .ToListAsync();

            Assert.That(files, Does.Contain("keep.cs"));
            Assert.That(files, Has.None.Matches<string>(f => f.StartsWith("vendor/")));
            Assert.That(files, Has.None.Matches<string>(f => f.EndsWith(".generated.cs")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task FileCrawler_WithNoAdditionalExclusions_ReturnsAllFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "// a");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");

            var crawler = new FileCrawler(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCrawler>.Instance);

            var files = await crawler.WalkAsync(dir).ToListAsync();
            Assert.That(files, Has.Count.EqualTo(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void LanguageDetector_WithOverrides_ReturnsOverriddenLanguage()
    {
        var overrides = new Dictionary<string, Language>
        {
            [".ts"] = Language.JavaScript,
            [".md"] = Language.Text,
        };

        Assert.That(LanguageDetector.Detect("file.ts", overrides: overrides), Is.EqualTo(Language.JavaScript));
        Assert.That(LanguageDetector.Detect("file.md", overrides: overrides), Is.EqualTo(Language.Text));
    }

    [Test]
    public void LanguageDetector_WithOverrides_StillFallsBackToBuiltIn()
    {
        var overrides = new Dictionary<string, Language>
        {
            [".ts"] = Language.JavaScript,
        };

        // .cs is not in overrides — should use built-in map
        Assert.That(LanguageDetector.Detect("file.cs", overrides: overrides), Is.EqualTo(Language.CSharp));
    }

    [Test]
    public void LanguageDetector_WithNullOverrides_ReturnsBuiltIn()
    {
        Assert.That(LanguageDetector.Detect("file.cs", overrides: null), Is.EqualTo(Language.CSharp));
        Assert.That(LanguageDetector.Detect("file.py", overrides: null), Is.EqualTo(Language.Python));
    }

    [Test]
    public async Task FileCrawler_WithAdditionalDirExclusion_ExcludesDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "generated"));
            File.WriteAllText(Path.Combine(dir, "generated", "output.cs"), "// generated");
            File.WriteAllText(Path.Combine(dir, "normal.cs"), "// normal");

            var crawler = new FileCrawler(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCrawler>.Instance);

            var exclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "generated/" };
            var files = await crawler.WalkAsync(dir, additionalExclusions: exclusions)
                .Select(f => f.RelativePath.Replace('\\', '/'))
                .ToListAsync();

            Assert.That(files, Does.Contain("normal.cs"));
            Assert.That(files, Has.None.Matches<string>(f => f.StartsWith("generated/")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
