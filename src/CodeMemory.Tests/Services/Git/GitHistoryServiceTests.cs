using CodeMemory.Services.Git;
using CodeMemory.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CodeMemory.Tests.Services.Git;

public sealed class GitHistoryServiceTests
{
    static string createTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryGitTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        runGit("init", dir);
        runGit("config user.name testuser", dir);
        runGit("config user.email test@test.com", dir);

        File.WriteAllText(Path.Combine(dir, "test.cs"), "public class TestClass { }");
        runGit("add test.cs", dir);
        runGit("commit -m \"First commit\"", dir);

        File.WriteAllText(Path.Combine(dir, "test.cs"), "public class TestClass { public void Method() { } }");
        runGit("commit -am \"Second commit\"", dir);

        File.WriteAllText(Path.Combine(dir, "other.cs"), "public class Other { }");
        runGit("add other.cs", dir);
        runGit("commit -m \"Add other file\"", dir);

        return dir;
    }

    static void runGit(string args, string workDir)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    static IStorageService createStorage(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddCodeMemoryStorage($"Data Source={dbPath}");
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IStorageService>();
    }

    [Test]
    public async Task GetSymbolHistoryAsync_ReturnsNull_WhenSymbolNotFound()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "CodeMemoryTests", Guid.NewGuid().ToString() + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var service = new GitHistoryService(storage, NullLogger<GitHistoryService>.Instance, Path.GetTempPath());
        var result = await service.GetSymbolHistoryAsync("NonExistent");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetSymbolHistoryAsync_ReturnsHistory_ForKnownSymbol()
    {
        var repoDir = createTempRepo();
        var dbPath = Path.Combine(repoDir, "test.db");
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "TestClass",
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        var service = new GitHistoryService(storage, NullLogger<GitHistoryService>.Instance, repoDir);
        var result = await service.GetSymbolHistoryAsync("TestClass");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.FilePath, Is.EqualTo("test.cs"));
        Assert.That(result.TotalCommits, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.UniqueAuthors, Is.EqualTo(1));
    }

    [Test]
    public async Task GetHotspotsAsync_ReturnsOrderedResults()
    {
        var repoDir = createTempRepo();
        var dbPath = Path.Combine(repoDir, "test.db");
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var service = new GitHistoryService(storage, NullLogger<GitHistoryService>.Instance, repoDir);
        var hotspots = await service.GetHotspotsAsync(5, 10);

        Assert.That(hotspots, Is.Not.Empty);
        Assert.That(hotspots.Count, Is.LessThanOrEqualTo(5));
        Assert.That(hotspots[0].CommitCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetSymbolHistoryAsync_RecentCommits_ArePopulated()
    {
        var repoDir = createTempRepo();
        var dbPath = Path.Combine(repoDir, "test.db");
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        await storage.StoreSymbolsAsync([
            new SymbolRecord
            {
                Id = "TestClass",
                Name = "TestClass",
                Kind = "Class",
                FilePath = "test.cs",
                FullName = "TestClass",
                LineStart = 1, LineEnd = 1,
            }
        ]);

        var service = new GitHistoryService(storage, NullLogger<GitHistoryService>.Instance, repoDir);
        var result = await service.GetSymbolHistoryAsync("TestClass", 10);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.RecentCommits, Is.Not.Null);
        Assert.That(result.RecentCommits.Count, Is.GreaterThan(0));
        Assert.That(result.RecentCommits[0].Author, Is.EqualTo("testuser"));
        Assert.That(result.RecentCommits[0].Hash, Has.Length.EqualTo(40));
    }
}
