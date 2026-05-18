using CodeMemory.AspNet.Storage;
using CodeMemory.AspNet.Storage.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodeMemory.Tests.Storage;

public sealed class CodeMemoryDbContextModelTests
{
    [Test]
    public void Model_UsesExpectedTablesColumnsKeysAndIndexes()
    {
        var options = new DbContextOptionsBuilder<CodeMemoryDbContext>()
            .UseSqlite("Data Source=:memory:")
            .ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
            .Options;

        using var db = new CodeMemoryDbContext(options, "main");

        var symbol = db.Model.FindEntityType(typeof(SymbolEntity))!;
        var relationship = db.Model.FindEntityType(typeof(RelationshipEntity))!;

        Assert.That(symbol.GetTableName(), Is.EqualTo("symbols"));
        Assert.That(relationship.GetTableName(), Is.EqualTo("relationships"));

        Assert.That(symbol.FindPrimaryKey()!.Properties.Select(p => p.Name), Is.EqualTo(new[] { nameof(SymbolEntity.Id) }));
        Assert.That(relationship.FindPrimaryKey()!.Properties.Select(p => p.Name), Is.EqualTo(new[] { nameof(RelationshipEntity.Id) }));

        AssertColumn(symbol, nameof(SymbolEntity.FilePath), "file_path");
        AssertColumn(symbol, nameof(SymbolEntity.Kind), "kind");
        AssertColumn(relationship, nameof(RelationshipEntity.SourceSymbolId), "source_symbol_id");
        AssertColumn(relationship, nameof(RelationshipEntity.TargetSymbolId), "target_symbol_id");
        AssertColumn(relationship, nameof(RelationshipEntity.RelationshipType), "relationship_type");

        AssertIndex(symbol, "IX_Symbols_FilePath", nameof(SymbolEntity.FilePath));
        AssertIndex(symbol, "IX_Symbols_Kind", nameof(SymbolEntity.Kind));
        AssertIndex(relationship, "IX_Relationships_Source", nameof(RelationshipEntity.SourceSymbolId));
        AssertIndex(relationship, "IX_Relationships_Target", nameof(RelationshipEntity.TargetSymbolId));
        AssertIndex(relationship, "IX_Relationships_Type", nameof(RelationshipEntity.RelationshipType));
    }

    static void AssertColumn(Microsoft.EntityFrameworkCore.Metadata.IEntityType entity, string propertyName, string columnName)
        => Assert.That(entity.FindProperty(propertyName)!.GetColumnName(), Is.EqualTo(columnName));

    static void AssertIndex(Microsoft.EntityFrameworkCore.Metadata.IEntityType entity, string indexName, params string[] propertyNames)
    {
        var index = entity.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == indexName);
        Assert.That(index, Is.Not.Null, $"Missing index {indexName}");
        Assert.That(index!.Properties.Select(p => p.Name), Is.EqualTo(propertyNames));
    }
}
