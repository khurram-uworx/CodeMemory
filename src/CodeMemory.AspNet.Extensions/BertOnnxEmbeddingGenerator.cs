using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;
using System.Numerics.Tensors;

namespace CodeMemory.AspNet.Extensions;

public sealed class BertOnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    static float[] meanPoolAndNormalize(
        ReadOnlySpan<float> outputData,
        ReadOnlySpan<long> attentionMask,
        int batchIndex,
        int batchSize,
        int seqLen,
        int dim)
    {
        var pooled = new float[dim];
        int validTokens = 0;
        int batchOffset = batchIndex * seqLen;

        for (int s = 0; s < seqLen; s++)
        {
            if (attentionMask[batchOffset + s] == 0)
                continue;

            validTokens++;
            int tokenOffset = (batchIndex * seqLen + s) * dim;
            for (int d = 0; d < dim; d++)
                pooled[d] += outputData[tokenOffset + d];
        }

        if (validTokens > 0)
        {
            float invCount = 1f / validTokens;
            for (int d = 0; d < dim; d++)
                pooled[d] *= invCount;
        }

        var norm = MathF.Sqrt(TensorPrimitives.SumOfSquares(pooled));
        if (norm > 0)
            TensorPrimitives.Divide(pooled, norm, pooled);

        return pooled;
    }

    readonly ILogger<BertOnnxEmbeddingGenerator> logger;
    readonly InferenceSession session;
    readonly BertTokenizer tokenizer;
    readonly int dimension;
    readonly int inferenceBatchSize;
    readonly EmbeddingGeneratorMetadata metadata;

    public BertOnnxEmbeddingGenerator(
        string modelPath,
        string vocabPath,
        ILogger<BertOnnxEmbeddingGenerator> logger,
        int intraOpThreads = 1,
        int inferenceBatchSize = 32)
    {
        this.logger = logger;
        this.inferenceBatchSize = inferenceBatchSize;

        logger.LogInformation("Loading ONNX model from {ModelPath} (intraOpThreads={Threads}, batchSize={Batch})",
            modelPath, intraOpThreads, inferenceBatchSize);
        var sw = Stopwatch.StartNew();

        var opts = new SessionOptions();
        opts.IntraOpNumThreads = intraOpThreads;
        opts.InterOpNumThreads = intraOpThreads;
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        session = new InferenceSession(modelPath, opts);

        logger.LogInformation("ONNX model loaded in {Elapsed}ms, session input names: {Names}",
            sw.ElapsedMilliseconds, string.Join(", ", session.InputNames));

        tokenizer = new BertTokenizer(vocabPath);
        logger.LogInformation("Tokenizer loaded with vocab size {VocabSize}, max length {MaxLen}",
            tokenizer.VocabSize, tokenizer.MaxLength);

        var outputMetadata = session.OutputMetadata;
        var outputKey = outputMetadata.Keys.First();
        var outputShape = outputMetadata[outputKey].Dimensions;
        dimension = outputShape.Length > 0
            ? (int)outputShape[^1]
            : 384;

        logger.LogInformation("Model output dimension: {Dim}, output key: {Key}", dimension, outputKey);

        metadata = new EmbeddingGeneratorMetadata(
            "bert-onnx-bge-micro-v2",
            defaultModelDimensions: dimension);
    }

    static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithDiagnostics(
        InferenceSession session,
        List<NamedOnnxValue> namedInputs,
        int seqLen,
        int batchSize,
        ILogger logger)
    {
        try
        {
            return session.Run(namedInputs);
        }
        catch (DllNotFoundException ex)
        {
            logger.LogError(ex, "OnnxRuntime native library not found");
            throw new InvalidOperationException(
                "OnnxRuntime native library (libonnxruntime.so) not found. " +
                "Ensure Dockerfile publishes with '-r linux-x64' and installs libgomp1.", ex);
        }
        catch (OnnxRuntimeException ex)
        {
            logger.LogError(ex, "OnnxRuntime error during inference: {Msg}", ex.Message);
            throw new InvalidOperationException(
                $"OnnxRuntime inference error: {ex.Message}. " +
                $"Model inputs: {namedInputs.Count}, seqLen={seqLen}, batchSize={batchSize}.", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during ONNX inference");
            throw new InvalidOperationException(
                $"OnnxRuntime inference failed: {ex.GetType().Name}: {ex.Message}. " +
                $"Model inputs: {namedInputs.Count}, seqLen={seqLen}, batchSize={batchSize}.", ex);
        }
    }

    List<float[]> processBatch(List<TokenizedSequence> batch, int dimension)
    {
        var maxSeqLen = Math.Min(
            batch.Max(i => i.AttentionMask.Count(m => m == 1)),
            tokenizer.MaxLength);
        var batchSize = batch.Count;
        var seqLen = maxSeqLen;

        var sw = Stopwatch.StartNew();

        var inputIdsArray = new long[batchSize * seqLen];
        var attentionMaskArray = new long[batchSize * seqLen];
        var tokenTypeIdsArray = new long[batchSize * seqLen];

        for (int b = 0; b < batchSize; b++)
        {
            var seq = batch[b];
            for (int s = 0; s < seqLen; s++)
            {
                var idx = b * seqLen + s;
                inputIdsArray[idx] = seq.InputIds[s];
                attentionMaskArray[idx] = seq.AttentionMask[s];
                tokenTypeIdsArray[idx] = seq.TokenTypeIds[s];
            }
        }

        var shape = new[] { batchSize, seqLen };
        var inputNames = session.InputNames;

        var namedInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputNames[0],
                new DenseTensor<long>(inputIdsArray, shape)),
            NamedOnnxValue.CreateFromTensor(inputNames[1],
                new DenseTensor<long>(attentionMaskArray, shape)),
            NamedOnnxValue.CreateFromTensor(inputNames[2],
                new DenseTensor<long>(tokenTypeIdsArray, shape)),
        };

        using var outputs = RunWithDiagnostics(session, namedInputs, seqLen, batchSize, logger);
        var outputKey = session.OutputNames[0];
        var outputValue = outputs.First(o => o.Name == outputKey);
        var outputTensor = outputValue.AsTensor<float>();
        var outputArray = outputTensor.ToArray();

        var results = new List<float[]>(batchSize);
        for (int b = 0; b < batchSize; b++)
        {
            var embedding = meanPoolAndNormalize(
                outputArray.AsSpan(), attentionMaskArray.AsSpan(),
                b, batchSize, seqLen, dimension);
            results.Add(embedding);
        }

        logger.LogDebug("Batch {Size}x{SeqLen} inferenced in {Elapsed}ms",
            batchSize, seqLen, sw.ElapsedMilliseconds);

        return results;
    }

    public void Dispose()
    {
        session.Dispose();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is not null
            ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata)
                ? metadata
                : serviceType?.IsInstanceOfType(this) == true
                    ? this
                    : null;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);

        var allInputs = values.Select(v => tokenizer.Tokenize(v ?? string.Empty)).ToList();
        logger.LogInformation("Generating embeddings for {Total} inputs in batches of {BatchSize}",
            allInputs.Count, inferenceBatchSize);

        var sw = Stopwatch.StartNew();
        var results = new GeneratedEmbeddings<Embedding<float>>(allInputs.Count);

        int batchCount = 0;
        for (int i = 0; i < allInputs.Count; i += inferenceBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = allInputs.GetRange(i, Math.Min(inferenceBatchSize, allInputs.Count - i));
            batchCount++;

            logger.LogInformation("ONNX batch {Batch}/{Total} ({Count} inputs)...",
                batchCount, (allInputs.Count + inferenceBatchSize - 1) / inferenceBatchSize, batch.Count);

            var embeddings = processBatch(batch, dimension);

            foreach (var emb in embeddings)
                results.Add(new Embedding<float>(emb));
        }

        logger.LogInformation("All {Total} embeddings generated in {Elapsed}ms",
            allInputs.Count, sw.ElapsedMilliseconds);

        return Task.FromResult(results);
    }
}
