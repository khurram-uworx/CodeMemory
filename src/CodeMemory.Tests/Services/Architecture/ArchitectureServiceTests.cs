using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using CodeMemory.Storage.Models;
using CodeMemory.Storage.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Services.Architecture;

public sealed class ArchitectureServiceTests
{
    static string getTempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CodeMemoryTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    static IStorageService createStorage(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddCodeMemoryStorage($"Data Source={dbPath}");
        return services.BuildServiceProvider().GetRequiredService<IStorageService>();
    }

    static ArchitectureService createService(IStorageService storage)
    {
        return new ArchitectureService(storage, NullLogger<ArchitectureService>.Instance);
    }

    [Test]
    public async Task GetOverviewAsync_ReturnsEmpty_WhenNoSymbols()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        Assert.That(overview.TotalFiles, Is.EqualTo(0));
        Assert.That(overview.TotalSymbols, Is.EqualTo(0));
        Assert.That(overview.TopLevelComponents, Is.Empty);
        Assert.That(overview.LanguageBreakdown, Is.Empty);
    }

    [Test]
    public async Task GetOverviewAsync_ReturnsCorrectCounts()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "c1", Name = "Class1", Kind = "Class", FilePath = "src/MyApp/Service.cs", FullName = "MyApp.Class1", LineStart = 1, LineEnd = 10 },
            new() { Id = "c2", Name = "Class2", Kind = "Class", FilePath = "src/MyApp/Service.cs", FullName = "MyApp.Class2", LineStart = 12, LineEnd = 20 },
            new() { Id = "c3", Name = "Util", Kind = "Class", FilePath = "src/MyLib/Util.cs", FullName = "MyLib.Util", LineStart = 1, LineEnd = 30 },
            new() { Id = "m1", Name = "Method1()", Kind = "Method", FilePath = "src/MyApp/Service.cs", FullName = "MyApp.Class1.Method1()", LineStart = 3, LineEnd = 8 },
            new() { Id = "m2", Name = "Method2()", Kind = "Method", FilePath = "src/MyLib/Util.cs", FullName = "MyLib.Util.Method2()", LineStart = 5, LineEnd = 10 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        Assert.That(overview.TotalFiles, Is.EqualTo(2));
        Assert.That(overview.TotalSymbols, Is.EqualTo(5));
    }

    [Test]
    public async Task GetOverviewAsync_GroupsByTopLevelDirectory()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "s1", Name = "Service1", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.Service1", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "Test1", Kind = "Class", FilePath = "tests/Test.cs", FullName = "Tests.Test1", LineStart = 1, LineEnd = 10 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        Assert.That(overview.TopLevelComponents, Has.Count.EqualTo(2));
        Assert.That(overview.TopLevelComponents.Any(c => c.Name == "src"), Is.True);
        Assert.That(overview.TopLevelComponents.Any(c => c.Name == "tests"), Is.True);
    }

    [Test]
    public async Task GetOverviewAsync_ComponentCountsAreCorrect()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "s1", Name = "C1", Kind = "Class", FilePath = "src/app/Service.cs", FullName = "App.C1", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "C2", Kind = "Class", FilePath = "src/app/Service.cs", FullName = "App.C2", LineStart = 12, LineEnd = 20 },
            new() { Id = "s3", Name = "C3", Kind = "Class", FilePath = "src/app/Other.cs", FullName = "App.C3", LineStart = 1, LineEnd = 15 },
            new() { Id = "s4", Name = "Lib", Kind = "Class", FilePath = "lib/Library.cs", FullName = "Lib.Lib", LineStart = 1, LineEnd = 50 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        var srcComponent = overview.TopLevelComponents.First(c => c.Name == "src");
        Assert.That(srcComponent.FileCount, Is.EqualTo(2));
        Assert.That(srcComponent.SymbolCount, Is.EqualTo(3));

        var libComponent = overview.TopLevelComponents.First(c => c.Name == "lib");
        Assert.That(libComponent.FileCount, Is.EqualTo(1));
        Assert.That(libComponent.SymbolCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetOverviewAsync_WithPath_FiltersResults()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "s1", Name = "AppService", Kind = "Class", FilePath = "src/App/Service.cs", FullName = "App.Service", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "TestService", Kind = "Class", FilePath = "tests/App/Test.cs", FullName = "Tests.Test", LineStart = 1, LineEnd = 10 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync("src");

        Assert.That(overview.TotalFiles, Is.EqualTo(1));
        Assert.That(overview.TotalSymbols, Is.EqualTo(1));
        Assert.That(overview.TopLevelComponents, Has.Count.EqualTo(1));
        Assert.That(overview.TopLevelComponents[0].Name, Is.EqualTo("src"));
    }

    [Test]
    public async Task GetOverviewAsync_LanguageBreakdown_DetectsCSharp()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "s1", Name = "C1", Kind = "Class", FilePath = "src/Service.cs", FullName = "App.C1", LineStart = 1, LineEnd = 10 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        Assert.That(overview.LanguageBreakdown, Does.ContainKey("C#"));
        Assert.That(overview.LanguageBreakdown["C#"], Is.EqualTo(1));
    }

    [Test]
    public async Task GetOverviewAsync_ComponentsAreOrderedBySymbolCount()
    {
        var dbPath = getTempDbPath();
        var storage = createStorage(dbPath);
        await storage.InitializeAsync();

        var symbols = new List<SymbolRecord>
        {
            new() { Id = "s1", Name = "S1", Kind = "Class", FilePath = "small/File.cs", FullName = "Small.S1", LineStart = 1, LineEnd = 10 },
            new() { Id = "s2", Name = "L1", Kind = "Class", FilePath = "large/File1.cs", FullName = "Large.L1", LineStart = 1, LineEnd = 10 },
            new() { Id = "s3", Name = "L2", Kind = "Class", FilePath = "large/File2.cs", FullName = "Large.L2", LineStart = 1, LineEnd = 10 },
            new() { Id = "s4", Name = "L3", Kind = "Class", FilePath = "large/File3.cs", FullName = "Large.L3", LineStart = 1, LineEnd = 10 },
        };

        await storage.StoreSymbolsAsync(symbols);

        var service = createService(storage);
        var overview = await service.GetOverviewAsync();

        Assert.That(overview.TopLevelComponents[0].Name, Is.EqualTo("large"));
        Assert.That(overview.TopLevelComponents[1].Name, Is.EqualTo("small"));
    }
}
