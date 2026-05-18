using CodeMemory.AspNet.Storage.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodeMemory.AspNet.Storage;

public sealed class CodeMemoryDbContext : DbContext
{
    public CodeMemoryDbContext(DbContextOptions<CodeMemoryDbContext> options, string schema)
        : base(options)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema is required.", nameof(schema));

        Schema = schema;
    }

    public string Schema { get; }

    public DbSet<SymbolEntity> Symbols => Set<SymbolEntity>();

    public DbSet<RelationshipEntity> Relationships => Set<RelationshipEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SymbolEntity>(entity =>
        {
            entity.ToTable("symbols");
            entity.HasKey(symbol => symbol.Id);

            entity.Property(symbol => symbol.Id).HasColumnName("id").IsRequired();
            entity.Property(symbol => symbol.Name).HasColumnName("name").IsRequired();
            entity.Property(symbol => symbol.Kind).HasColumnName("kind").IsRequired();
            entity.Property(symbol => symbol.FilePath).HasColumnName("file_path").IsRequired();
            entity.Property(symbol => symbol.LineStart).HasColumnName("line_start");
            entity.Property(symbol => symbol.LineEnd).HasColumnName("line_end");
            entity.Property(symbol => symbol.FullName).HasColumnName("full_name").IsRequired();
            entity.Property(symbol => symbol.Modifiers).HasColumnName("modifiers");
            entity.Property(symbol => symbol.Documentation).HasColumnName("documentation");

            entity.HasIndex(symbol => symbol.FilePath).HasDatabaseName("IX_Symbols_FilePath");
            entity.HasIndex(symbol => symbol.Kind).HasDatabaseName("IX_Symbols_Kind");
        });

        modelBuilder.Entity<RelationshipEntity>(entity =>
        {
            entity.ToTable("relationships");
            entity.HasKey(relationship => relationship.Id);

            entity.Property(relationship => relationship.Id).HasColumnName("id").IsRequired();
            entity.Property(relationship => relationship.SourceSymbolId).HasColumnName("source_symbol_id").IsRequired();
            entity.Property(relationship => relationship.TargetSymbolId).HasColumnName("target_symbol_id").IsRequired();
            entity.Property(relationship => relationship.RelationshipType).HasColumnName("relationship_type").IsRequired();

            entity.HasIndex(relationship => relationship.SourceSymbolId).HasDatabaseName("IX_Relationships_Source");
            entity.HasIndex(relationship => relationship.TargetSymbolId).HasDatabaseName("IX_Relationships_Target");
            entity.HasIndex(relationship => relationship.RelationshipType).HasDatabaseName("IX_Relationships_Type");
        });
    }
}

public sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is CodeMemoryDbContext codeMemoryContext
            ? (context.GetType(), codeMemoryContext.Schema, designTime)
            : (object)(context.GetType(), designTime);
}
