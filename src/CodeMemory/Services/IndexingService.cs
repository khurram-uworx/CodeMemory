using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Storage.Models;
using CodeMemory.Storage.Services;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.AI;
using System.Numerics.Tensors;
using System.Text.Json;

namespace CodeMemory.Services;

public sealed class IndexingService : BackgroundService
{
    static SymbolRecord mapToSymbolRecord(Symbol s)
    {
        return new SymbolRecord
        {
            Id = s.FullName,
            Name = s.Name,
            Kind = s.Kind.ToString(),
            FilePath = s.FilePath,
            LineStart = s.LineRange.Start,
            LineEnd = s.LineRange.End,
            FullName = s.FullName,
            Modifiers = s.Modifiers.Count > 0 ? string.Join(",", s.Modifiers) : null,
            Documentation = s.Documentation,
        };
    }

    static ChunkRecord mapToChunkRecord(DocumentChunk c, ReadOnlyMemory<float>? embedding)
    {
        return new ChunkRecord
        {
            Id = c.Id,
            SymbolId = c.SymbolId,
            FilePath = c.FilePath,
            Content = c.Content,
            Language = c.Language,
            LineStart = c.LineRange.Start,
            LineEnd = c.LineRange.End,
            MetadataJson = c.Metadata.Count > 0 ? JsonSerializer.Serialize(c.Metadata) : null,
            Embedding = embedding,
        };
    }

    static RelationshipRecord mapToRelationshipRecord(Relationship r)
    {
        return new RelationshipRecord
        {
            Id = $"{r.SourceSymbolId}->{r.TargetSymbolId}:{r.RelationshipType}",
            SourceSymbolId = r.SourceSymbolId,
            TargetSymbolId = r.TargetSymbolId,
            RelationshipType = r.RelationshipType,
        };
    }

    readonly ILogger<IndexingService> logger;
    readonly FileCrawler crawler;
    readonly ILanguageParser parser;
    readonly RoslynSymbolExtractor extractor;
    readonly RoslynRelationshipExtractor relationshipExtractor;
    readonly SemanticChunker chunker;
    readonly IStorageService storage;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;

    public IndexingService(ILogger<IndexingService> logger, FileCrawler crawler,
        ILanguageParser parser, RoslynSymbolExtractor extractor,
        RoslynRelationshipExtractor relationshipExtractor, SemanticChunker chunker,
        IStorageService storage,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        this.logger = logger;
        this.crawler = crawler;
        this.parser = parser;
        this.extractor = extractor;
        this.relationshipExtractor = relationshipExtractor;
        this.chunker = chunker;
        this.storage = storage;
        this.embeddingGenerator = embeddingGenerator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Indexing service starting — eager indexing of {RepoRoot}",
            Environment.CurrentDirectory);

        try
        {
            await storage.InitializeAsync(stoppingToken);

            var fileCount = 0;
            var parsedCount = 0;
            var symbolCount = 0;
            var chunkCount = 0;

            var allSymbolRecords = new List<SymbolRecord>();
            var allSymbols = new List<Symbol>();
            var allChunks = new List<DocumentChunk>();
            var syntaxTrees = new List<(SyntaxTree Tree, string FilePath)>();

            await foreach (var entry in crawler.WalkAsync(
                Environment.CurrentDirectory,
                cancellationToken: stoppingToken))
            {
                logger.LogDebug("Found file: {Path} ({Ext})", entry.RelativePath, entry.Extension);
                fileCount++;

                if (LanguageDetector.Detect(entry.Path) == Language.CSharp)
                {
                    var syntaxTree = await parser.ParseAsync(entry.Path, stoppingToken);
                    if (syntaxTree != null)
                    {
                        parsedCount++;
                        var symbols = extractor.Extract(syntaxTree, entry.Path);
                        symbolCount += symbols.Count;

                        allSymbolRecords.AddRange(symbols.Select(mapToSymbolRecord));
                        allSymbols.AddRange(symbols);
                        syntaxTrees.Add((syntaxTree, entry.Path));

                        var fileText = await File.ReadAllTextAsync(entry.Path, stoppingToken);
                        var chunks = chunker.ChunkAll(symbols, fileText, entry.Path, Language.CSharp);
                        chunkCount += chunks.Count;

                        allChunks.AddRange(chunks);

                        logger.LogDebug("Parsed: {Path} — {Symbols} symbols, {Chunks} chunks",
                            entry.RelativePath, symbols.Count, chunks.Count);
                    }
                }
            }

            if (allSymbolRecords.Count > 0)
            {
                await storage.StoreSymbolsAsync(allSymbolRecords, stoppingToken);
                logger.LogInformation("Stored {Count} symbol records", allSymbolRecords.Count);
            }

            if (allSymbols.Count > 0)
            {
                var allRelationships = new List<Relationship>();
                foreach (var (tree, path) in syntaxTrees)
                {
                    var rels = relationshipExtractor.ExtractRelationships(tree, allSymbols, path);
                    allRelationships.AddRange(rels);
                }

                if (allRelationships.Count > 0)
                {
                    await storage.StoreRelationshipsAsync(
                        allRelationships.Select(mapToRelationshipRecord).ToList(), stoppingToken);
                    logger.LogInformation("Stored {Count} relationship records", allRelationships.Count);
                }
            }

            if (allChunks.Count > 0 && embeddingGenerator != null)
            {
                logger.LogInformation("Generating embeddings for {Count} chunks...", allChunks.Count);
                var contents = allChunks.Select(c => c.Content).ToList();
                var generatedEmbeddings = await embeddingGenerator.GenerateAsync(contents, null, stoppingToken);

                var chunkRecords = new List<ChunkRecord>(allChunks.Count);
                for (int i = 0; i < allChunks.Count; i++)
                {
                    var vector = generatedEmbeddings[i].Vector;
                    var norm = TensorPrimitives.Norm(vector.Span);
                    var normalized = new float[vector.Length];
                    if (norm > 0)
                    {
                        for (int j = 0; j < vector.Length; j++)
                            normalized[j] = vector.Span[j] / norm;
                    }
                    chunkRecords.Add(mapToChunkRecord(allChunks[i], normalized));
                }

                await storage.StoreChunksAsync(chunkRecords, stoppingToken);
                logger.LogInformation("Stored {Count} chunk records with embeddings", chunkRecords.Count);
            }
            else if (allChunks.Count > 0)
            {
                logger.LogWarning("No embedding generator registered — skipping chunk storage. Register an IEmbeddingGenerator<string, Embedding<float>> to enable semantic chunk storage.");
            }

            logger.LogInformation(
                "Indexing complete — {Files} files, {Parsed} parsed, {Symbols} symbols, {Chunks} chunks, {Relationships} relationships",
                fileCount, parsedCount, symbolCount, chunkCount, allSymbols.Count > 0 ? "extracted" : "0");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Indexing service stopped");
        }
    }
}
