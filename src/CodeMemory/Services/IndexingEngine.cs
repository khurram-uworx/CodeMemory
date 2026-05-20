using CodeMemory.Indexing;
using CodeMemory.Indexing.Chunking;
using CodeMemory.Indexing.Extraction;
using CodeMemory.Indexing.Parsing;
using CodeMemory.Services.Architecture;
using CodeMemory.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.Json;

namespace CodeMemory.Services;

public sealed record FileIndexResult(
    string FilePath,
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<ChunkRecord> Chunks,
    IReadOnlyList<RelationshipRecord> Relationships);

public sealed class IndexingEngine
{
    static SymbolRecord mapToSymbolRecord(Symbol s, string guid)
        => new SymbolRecord
        {
            Id = guid,
            Name = s.Name,
            Kind = s.Kind.ToString(),
            FilePath = s.FilePath,
            LineStart = s.LineRange.Start,
            LineEnd = s.LineRange.End,
            FullName = s.FullName,
            Modifiers = s.Modifiers.Count > 0 ? string.Join(",", s.Modifiers) : null,
            Documentation = s.Documentation,
        };

    static ChunkRecord mapToChunkRecord(DocumentChunk c, ReadOnlyMemory<float>? embedding, IReadOnlyDictionary<string, string> fullNameToGuid)
        => new ChunkRecord
        {
            Id = c.Id,
            SymbolId = fullNameToGuid.TryGetValue(c.SymbolId, out var guid) ? guid : null,
            FilePath = c.FilePath,
            Content = c.Content,
            Language = c.Language,
            LineStart = c.LineRange.Start,
            LineEnd = c.LineRange.End,
            MetadataJson = c.Metadata.Count > 0 ? JsonSerializer.Serialize(c.Metadata) : null,
            Embedding = embedding,
        };

    static RelationshipRecord mapToRelationshipRecord(Relationship r,
        IReadOnlyDictionary<string, string> fullNameToGuid)
    {
        var sourceId = fullNameToGuid[r.SourceSymbolId];
        var targetId = fullNameToGuid[r.TargetSymbolId];
        return new RelationshipRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Id = $"{sourceId}->{targetId}:{r.RelationshipType}",
            RelationshipType = r.RelationshipType,
        };
    }

    readonly ILogger<IndexingEngine> logger;
    readonly FileCrawler crawler;
    readonly Dictionary<Language, ILanguageParser> parsers;
    readonly Dictionary<Language, (ISymbolExtractor, IRelationshipExtractor)> extractors;
    readonly SemanticChunker chunker;
    readonly IStorageService storage;
    readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;
    readonly ProjectFileDetector projectFileDetector;

    public IndexingEngine(ILogger<IndexingEngine> logger, FileCrawler crawler,
        RoslynCSharpParser roslynParser, TreeSitterParser tsParser,
        RoslynSymbolExtractor roslynExtractor, RoslynRelationshipExtractor roslynRelationshipExtractor,
        TreeSitterSymbolExtractor tsExtractor, TreeSitterRelationshipExtractor tsRelationshipExtractor,
        SemanticChunker chunker, IStorageService storage,
        ProjectFileDetector projectFileDetector,
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
            [Language.Python] = tsParser,
            [Language.Go] = tsParser,
            [Language.Rust] = tsParser,
            [Language.HTML] = tsParser,
        };
        extractors = new Dictionary<Language, (ISymbolExtractor, IRelationshipExtractor)>
        {
            [Language.CSharp] = (roslynExtractor, roslynRelationshipExtractor),
            [Language.TypeScript] = (tsExtractor, tsRelationshipExtractor),
            [Language.JavaScript] = (tsExtractor, tsRelationshipExtractor),
            [Language.Java] = (tsExtractor, tsRelationshipExtractor),
            [Language.Python] = (tsExtractor, tsRelationshipExtractor),
            [Language.Go] = (tsExtractor, tsRelationshipExtractor),
            [Language.Rust] = (tsExtractor, tsRelationshipExtractor),
            [Language.HTML] = (tsExtractor, tsRelationshipExtractor),
        };
        this.chunker = chunker;
        this.storage = storage;
        this.projectFileDetector = projectFileDetector;
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
        var fullNameToGuid = new Dictionary<string, string>();

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

                    foreach (var s in symbols)
                    {
                        if (!fullNameToGuid.ContainsKey(s.FullName))
                            fullNameToGuid[s.FullName] = Guid.NewGuid().ToString("N");

                        allSymbolRecords.Add(mapToSymbolRecord(s, fullNameToGuid[s.FullName]));
                    }

                    allSymbols.AddRange(symbols);
                    parseResults.Add((result, entry.RelativePath));

                    var fileText = result.FileText;
                    var chunks = chunker.ChunkAll(symbols, fileText, entry.RelativePath, lang);
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
                    allRelationships.Select(r => mapToRelationshipRecord(r, fullNameToGuid)).ToList(), ct);
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

                chunkRecords.Add(mapToChunkRecord(allChunks[i], normalized, fullNameToGuid));
            }

            await storage.StoreChunksAsync(chunkRecords, ct);
            logger.LogInformation("Stored {Count} chunk records with embeddings", chunkRecords.Count);
        }
        else if (allChunks.Count > 0)
            logger.LogWarning("No embedding generator registered — skipping chunk storage. Register an IEmbeddingGenerator<string, Embedding<float>> to enable semantic chunk storage.");

        var componentMapping = projectFileDetector.Discover(repoRoot);
        if (componentMapping.Count > 0)
        {
            ComponentMapping.Initialize(componentMapping);
            logger.LogInformation("Project file detection: discovered {Count} components", componentMapping.Count);
        }

        logger.LogInformation(
            "Indexing complete — {Files} files, {Parsed} parsed, {Symbols} symbols, {Chunks} chunks, {Relationships} relationships",
            fileCount, parsedCount, symbolCount, chunkCount, allSymbols.Count > 0 ? "extracted" : "0");
    }

    public async Task<FileIndexResult> ProcessFileAsync(string filePath, CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath);
        var lang = LanguageDetector.Detect(filePath);

        if (lang == Language.Unknown || !parsers.TryGetValue(lang, out var parser))
        {
            logger.LogDebug("Skipping unsupported file: {Path}", filePath);
            return new FileIndexResult(filePath, [], [], []);
        }

        var result = await parser.ParseAsync(filePath, ct);
        if (result == null)
        {
            logger.LogDebug("Parser returned null for: {Path}", filePath);
            return new FileIndexResult(filePath, [], [], []);
        }

        var rootUri = new Uri(storage.RepoRoot + Path.DirectorySeparatorChar);
        var fileUri = new Uri(filePath);
        var relativePath = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);

        var (symbolExtractor, relationshipExtractor) = extractors[lang];
        var symbols = symbolExtractor.Extract(result, relativePath);

        if (symbols.Count == 0)
        {
            logger.LogDebug("No symbols extracted from: {Path}", relativePath);
            return new FileIndexResult(filePath, [], [], []);
        }

        var fullNameToGuid = new Dictionary<string, string>(symbols.Count);
        var symbolRecords = new List<SymbolRecord>(symbols.Count);

        foreach (var s in symbols)
        {
            var guid = Guid.NewGuid().ToString("N");
            fullNameToGuid[s.FullName] = guid;
            symbolRecords.Add(mapToSymbolRecord(s, guid));
        }

        var relationshipRecords = new List<RelationshipRecord>();
        try
        {
            var relationships = relationshipExtractor.ExtractRelationships(result, symbols, relativePath);
            relationshipRecords = relationships
                .Where(r => fullNameToGuid.ContainsKey(r.SourceSymbolId) && fullNameToGuid.ContainsKey(r.TargetSymbolId))
                .Select(r => mapToRelationshipRecord(r, fullNameToGuid))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Relationship extraction failed for {Path}", relativePath);
        }

        var fileText = result.FileText;
        var chunks = chunker.ChunkAll(symbols, fileText, relativePath, lang);

        List<ChunkRecord> chunkRecords;
        if (chunks.Count > 0 && embeddingGenerator != null)
        {
            var contents = chunks.Select(c => c.Content).ToList();
            var generatedEmbeddings = await embeddingGenerator.GenerateAsync(contents, null, ct);

            chunkRecords = new List<ChunkRecord>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var vector = generatedEmbeddings[i].Vector;
                var norm = TensorPrimitives.Norm(vector.Span);
                var normalized = new float[vector.Length];

                if (norm > 0)
                    for (int j = 0; j < vector.Length; j++)
                        normalized[j] = vector.Span[j] / norm;

                chunkRecords.Add(mapToChunkRecord(chunks[i], normalized, fullNameToGuid));
            }
        }
        else
        {
            chunkRecords = chunks.Select(c => mapToChunkRecord(c, null, fullNameToGuid)).ToList();
            if (chunks.Count > 0 && embeddingGenerator == null)
                logger.LogDebug("No embedding generator — stored chunks without embeddings for {Path}", relativePath);
        }

        logger.LogDebug(
            "Processed file {Path} — {Symbols} symbols, {Chunks} chunks, {Relationships} relationships",
            relativePath, symbolRecords.Count, chunkRecords.Count, relationshipRecords.Count);

        return new FileIndexResult(filePath, symbolRecords, chunkRecords, relationshipRecords);
    }
}
