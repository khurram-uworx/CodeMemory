using CodeMemory.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Mcp;

public sealed class CodeMemoryInitServiceTests
{
    static string createTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryInitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void Run_NoExistingFiles_CreatesConfigAndGitIgnore()
    {
        var repoRoot = createTempDir();
        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);

        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.ConfigCreated, Is.True);
        Assert.That(result.GitIgnoreUpdated, Is.True);
        Assert.That(File.Exists(Path.Combine(repoRoot, ".codememory.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(repoRoot, ".gitignore")), Is.True);

        var gitIgnore = File.ReadAllText(Path.Combine(repoRoot, ".gitignore"));
        Assert.That(gitIgnore, Does.Contain(".codememory.json"));
    }

    [Test]
    public void Run_ExistingConfig_SkipsCreation()
    {
        var repoRoot = createTempDir();
        var configPath = Path.Combine(repoRoot, ".codememory.json");
        File.WriteAllText(configPath, """{"exclude": ["custom"]}""");
        var originalContent = File.ReadAllText(configPath);

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.ConfigCreated, Is.False);
        Assert.That(result.ConfigPath, Is.EqualTo(configPath));
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(originalContent));
    }

    [Test]
    public void Run_ExistingGitIgnoreWithCoverage_SkipsUpdate()
    {
        var repoRoot = createTempDir();
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), ".codememory.json");

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.ConfigCreated, Is.True);
        Assert.That(result.GitIgnoreUpdated, Is.False);
    }

    [Test]
    public void Run_ExistingGitIgnoreWithoutCoverage_AppendsEntry()
    {
        var repoRoot = createTempDir();
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "bin/\nobj/");

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.ConfigCreated, Is.True);
        Assert.That(result.GitIgnoreUpdated, Is.True);

        var gitIgnore = File.ReadAllText(Path.Combine(repoRoot, ".gitignore"));
        Assert.That(gitIgnore, Does.Contain(".codememory.json"));
    }

    [Test]
    public void Run_ConfigCreatedWithDefaultTemplate()
    {
        var repoRoot = createTempDir();
        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);

        service.Run(repoRoot);

        var configJson = File.ReadAllText(Path.Combine(repoRoot, ".codememory.json"));
        Assert.That(configJson, Does.Contain("\"exclude\""));
        Assert.That(configJson, Does.Contain("\"languageOverrides\""));
        Assert.That(configJson, Does.Contain("\"clusteringThreshold\""));
    }

    [Test]
    public void Run_ConfigAndGitIgnoreAlreadyExist_ReportsNothingToDo()
    {
        var repoRoot = createTempDir();
        File.WriteAllText(Path.Combine(repoRoot, ".codememory.json"), "{}");
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), ".codememory.json");

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.ConfigCreated, Is.False);
        Assert.That(result.GitIgnoreUpdated, Is.False);
        Assert.That(result.Message, Does.Contain("nothing to do"));
    }

    [Test]
    public void Run_CreatesGitIgnoreWhenMissing()
    {
        var repoRoot = createTempDir();
        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);

        var result = service.Run(repoRoot);

        Assert.That(result.Status, Is.EqualTo("ok"));
        Assert.That(result.GitIgnoreUpdated, Is.True);
        Assert.That(File.Exists(Path.Combine(repoRoot, ".gitignore")), Is.True);
    }

    [Test]
    public void Run_WildcardPatternInGitIgnore_DetectsCoverage()
    {
        var repoRoot = createTempDir();
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "*.json");

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.GitIgnoreUpdated, Is.False);
    }

    [Test]
    public void Run_ReturnsError_WhenConfigPathIsFile()
    {
        var repoRoot = createTempDir();
        var configPath = Path.Combine(repoRoot, ".codememory.json");
        File.WriteAllText(configPath, "not valid json but that's not the point");

        var service = new CodeMemoryInitService(NullLogger<CodeMemoryInitService>.Instance);
        var result = service.Run(repoRoot);

        Assert.That(result.ConfigCreated, Is.False);
        Assert.That(result.Status, Is.EqualTo("ok"));
    }

    [Test]
    public void Run_WithoutLogger_DoesNotThrow()
    {
        var repoRoot = createTempDir();
        var service = new CodeMemoryInitService(null);

        Assert.DoesNotThrow(() => service.Run(repoRoot));

        Assert.That(File.Exists(Path.Combine(repoRoot, ".codememory.json")), Is.True);
    }
}
