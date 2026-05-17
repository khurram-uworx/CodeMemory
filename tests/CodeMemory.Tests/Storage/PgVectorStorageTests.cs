using CodeMemory.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Storage;

[TestFixture]
[Category("Integration")]
[Explicit("PgVector required")]
public sealed class PgVectorStorageTests
{
    const string ConnectionString = "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

    [Test]
    public async Task CreatePgVectorStorage_InitializesCollections()
    {
        var storage = AspNet.Storage.ServiceCollectionExtensions.CreatePgVectorStorage(
            Environment.CurrentDirectory,
            ConnectionString,
            "cm_test",
            NullLogger<StorageService>.Instance);

        await storage.InitializeAsync();

        var symbol = new SymbolRecord
        {
            Id = "test-symbol",
            Name = "TestClass",
            Kind = "Class",
            FilePath = "test.cs",
            LineStart = 1,
            LineEnd = 10,
            FullName = "TestClass"
        };

        await storage.StoreSymbolsAsync([symbol]);
        var result = await storage.GetSymbolAsync("test-symbol");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("TestClass"));
    }

    [Test]
    public async Task PgVector_ReposIsolatedBySchema()
    {
        var storage1 = AspNet.Storage.ServiceCollectionExtensions.CreatePgVectorStorage(
            "C:\\repo1", ConnectionString, "cm_repo1",
            NullLogger<StorageService>.Instance);

        var storage2 = AspNet.Storage.ServiceCollectionExtensions.CreatePgVectorStorage(
            "C:\\repo2", ConnectionString, "cm_repo2",
            NullLogger<StorageService>.Instance);

        await storage1.InitializeAsync();
        await storage2.InitializeAsync();

        await storage1.StoreSymbolsAsync([new SymbolRecord
        {
            Id = "s1", Name = "Repo1Class", Kind = "Class",
            FilePath = "a.cs", LineStart = 1, LineEnd = 5, FullName = "Repo1Class"
        }]);

        await storage2.StoreSymbolsAsync([new SymbolRecord
        {
            Id = "s2", Name = "Repo2Class", Kind = "Class",
            FilePath = "b.cs", LineStart = 1, LineEnd = 5, FullName = "Repo2Class"
        }]);

        var fromRepo1 = await storage1.GetSymbolAsync("s1");
        var fromRepo2 = await storage2.GetSymbolAsync("s2");
        var crossRepo = await storage2.GetSymbolAsync("s1");

        Assert.That(fromRepo1, Is.Not.Null);
        Assert.That(fromRepo2, Is.Not.Null);
        Assert.That(crossRepo, Is.Null, "Repo2 should not see Repo1's symbols (schema isolation)");
    }
}
