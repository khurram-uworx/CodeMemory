using CodeMemory.AspNet.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeMemory.Tests.Storage;

public sealed class StorageProviderSelectionTests
{
    [Test]
    public void CreateStorage_ReturnsNullForInMemoryProviderSoProgramCanUseFallback()
    {
        var repoRoot = CreateTempRepoRoot();
        var builder = WebApplication.CreateBuilder();

        try
        {
            var (provider, dbPath, storage) = builder.CreateStorage(
                "inmemory",
                "codememory",
                repoRoot,
                NullLoggerFactory.Instance);

            Assert.That(provider, Is.EqualTo("inmemory"));
            Assert.That(dbPath, Is.Null);
            Assert.That(storage, Is.Null);
        }
        finally
        {
            Cleanup(repoRoot);
        }
    }

    [Test]
    public void CreateStorage_ReturnsHybridStorageForSqliteProvider()
    {
        var repoRoot = CreateTempRepoRoot();
        var builder = WebApplication.CreateBuilder();

        try
        {
            var (provider, dbPath, storage) = builder.CreateStorage(
                "sqlite",
                "codememory",
                repoRoot,
                NullLoggerFactory.Instance);

            Assert.That(provider, Is.EqualTo("sqlite"));
            Assert.That(dbPath, Is.EqualTo(Path.Combine(repoRoot, ".memorycode", "sqlvec.db")));
            Assert.That(storage, Is.TypeOf<HybridStorageService>());
        }
        finally
        {
            Cleanup(repoRoot);
        }
    }

    static string CreateTempRepoRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "CodeMemoryProviderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        return path;
    }

    static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary test directories.
        }
    }
}
