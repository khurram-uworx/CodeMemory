using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.Json;

namespace CodeMemory.Services;

public sealed class IndexingEngine
{
    static SymbolRecord mapToSymbolRecord(Symbol s)
        => new SymbolRecord
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

    static ChunkRecord mapToChunkRecord(DocumentChunk c, ReadOnlyMemory<float>? embedding)
        => new ChunkRecord
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

    static RelationshipRecord mapToRelationshipRecord(Relationship r)
        => new RelationshipRecord
        {
            Id = $"{r.SourceSymbolId}->{r.TargetSymbolId}:{r.RelationshipType}",
            SourceSymbolId = r.SourceSymbolId,
            TargetSymbolId = r.TargetSymbolId,
            RelationshipType = r.RelationshipType,
        };

    readonly ILogger<IndexingEngine> logger;
    readonly FileCrawler crawler;
    readonly Dictionary<Language, ILanguageParser> parsers;
    readonly Dictionary<Language, (ISymbolExtractor, IRelationshipExtractor)> extractors;
    readonly SemanticChunker chunker;
    readonly IStorageService storage;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;

    public IndexingEngine(ILogger<IndexingEngine> logger, FileCrawler crawler,
        RoslynCSharpParser roslynParser, TreeSitterParser tsParser,
        RoslynSymbolExtractor roslynExtractor, RoslynRelationshipExtractor roslynRelationshipExtractor,
        TreeSitterSymbolExtractor tsExtractor, TreeSitterRelationshipExtractor tsRelationshipExtractor,
        SemanticChunker chunker, IStorageService storage,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        this.logger = logger;
        this.crawler = crawler;
        parsers = new Dictionary<Language, ILanguageParser>
        {
            [Language.CSharp] = roslynParser,
            [Language.TypeScript] = tsParser,
            [Language.JavaScript] = tsParser,
            [Language.Java] = tsParser,
        };
        extractors = new Dictionary<Language, (ISymbolExtractor, IRelationshipExtractor)>
        {
            [Language.CSharp] = (roslynExtractor, roslynRelationshipExtractor),
            [Language.TypeScript] = (tsExtractor, tsRelationshipExtractor),
            [Language.JavaScript] = (tsExtractor, tsRelationshipExtractor),
            [Language.Java] = (tsExtractor, tsRelationshipExtractor),
        };
        this.chunker = chunker;
        this.storage = storage;
        this.embeddingGenerator = embeddingGenerator;
    }

    public async Task RunIndexingAsync(string repoRoot, CancellationToken ct)
    {
        logger.LogInformation("Indexing engine starting — eager indexing of {RepoRoot}",
            repoRoot);

        await storage.InitializeAsync(ct);

        long fileCount = 0;
        long parsedCount = 0;
        long symbolCount = 0;
        long chunkCount = 0;

        var allSymbolRecords = new List<SymbolRecord>();
        var allSymbols = new List<Symbol>();
        var allChunks = new List<DocumentChunk>();
        var parseResults = new List<(ParseResult Result, string FilePath)>();

        await foreach (var entry in crawler.WalkAsync(repoRoot, cancellationToken: ct))
        {
            logger.LogDebug("Found file: {Path} ({Ext})", entry.RelativePath, entry.Extension);
            fileCount++;

            var lang = LanguageDetector.Detect(entry.Path);
            if (lang != Language.Unknown && parsers.TryGetValue(lang, out var languageParser))
            {
                var result = await languageParser.ParseAsync(entry.Path, ct);

                if (result != null)
                {
                    parsedCount++;
                    var (symbolExtractor, _) = extractors[lang];
                    var symbols = symbolExtractor.Extract(result, entry.RelativePath);
                    symbolCount += symbols.Count;

                    allSymbolRecords.AddRange(symbols.Select(mapToSymbolRecord));
                    allSymbols.AddRange(symbols);
                    parseResults.Add((result, entry.Path));

                    var fileText = result.FileText;
                    var chunks = chunker.ChunkAll(symbols, fileText, entry.Path, lang);
                    chunkCount += chunks.Count;

                    allChunks.AddRange(chunks);

                    logger.LogDebug("Parsed: {Path} — {Symbols} symbols, {Chunks} chunks",
                        entry.RelativePath, symbols.Count, chunks.Count);
                }
            }
        }

        if (allSymbolRecords.Count > 0)
        {
            var stopwatch = new Stopwatch();
            await storage.StoreSymbolsAsync(allSymbolRecords, ct);
            logger.LogInformation("Stored {Count} symbol records, took {Elapsed}",
                allSymbolRecords.Count, stopwatch.Elapsed);
        }

        if (allSymbols.Count > 0)
        {
            var allRelationships = new List<Relationship>();
            foreach (var (result, path) in parseResults)
            {
                if (extractors.TryGetValue(result.Language, out var pair))
                {
                    var rels = pair.Item2.ExtractRelationships(result, allSymbols, path);
                    allRelationships.AddRange(rels);
                }
            }

            if (allRelationships.Count > 0)
            {
                var stopWatch = new Stopwatch();
                await storage.StoreRelationshipsAsync(
                    allRelationships.Select(mapToRelationshipRecord).ToList(), ct);
                logger.LogInformation("Stored {Count} relationship records, took {Elapsed}",
                    allRelationships.Count, stopWatch.Elapsed);
            }
        }

        if (allChunks.Count > 0 && embeddingGenerator != null)
        {
            logger.LogInformation("Generating embeddings for {Count} chunks...", allChunks.Count);
            var contents = allChunks.Select(c => c.Content).ToList();
            var generatedEmbeddings = await embeddingGenerator.GenerateAsync(contents, null, ct);

            var chunkRecords = new List<ChunkRecord>(allChunks.Count);
            for (int i = 0; i < allChunks.Count; i++)
            {
                var vector = generatedEmbeddings[i].Vector;
                var norm = TensorPrimitives.Norm(vector.Span);
                var normalized = new float[vector.Length];

                if (norm > 0)
                    for (int j = 0; j < vector.Length; j++)
                        normalized[j] = vector.Span[j] / norm;

                chunkRecords.Add(mapToChunkRecord(allChunks[i], normalized));
            }

            await storage.StoreChunksAsync(chunkRecords, ct);
            logger.LogInformation("Stored {Count} chunk records with embeddings", chunkRecords.Count);
        }
        else if (allChunks.Count > 0)
            logger.LogWarning("No embedding generator registered — skipping chunk storage. Register an IEmbeddingGenerator<string, Embedding<float>> to enable semantic chunk storage.");

        logger.LogInformation(
            "Indexing complete — {Files} files, {Parsed} parsed, {Symbols} symbols, {Chunks} chunks, {Relationships} relationships",
            fileCount, parsedCount, symbolCount, chunkCount, allSymbols.Count > 0 ? "extracted" : "0");
    }
}
