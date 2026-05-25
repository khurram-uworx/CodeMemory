using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Numerics.Tensors;

namespace CodeMemory.AspNet.Extensions;

public sealed class BertOnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _dimension;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public BertOnnxEmbeddingGenerator(string modelPath, string vocabPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = new BertTokenizer(vocabPath);

        var outputMetadata = _session.OutputMetadata;
        var outputKey = outputMetadata.Keys.First();
        var outputShape = outputMetadata[outputKey].Dimensions;
        _dimension = outputShape.Length > 0
            ? (int)outputShape[^1]
            : 384;

        _metadata = new EmbeddingGeneratorMetadata(
            "bert-onnx-bge-micro-v2",
            defaultModelDimensions: _dimension);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is not null
            ? null
            : serviceType == typeof(EmbeddingGeneratorMetadata)
                ? _metadata
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

        var results = new GeneratedEmbeddings<Embedding<float>>();
        var inputs = values.Select(v => _tokenizer.Tokenize(v ?? string.Empty)).ToList();

        var maxBatchSeqLen = inputs.Count > 0
            ? inputs.Max(i => i.AttentionMask.Count(m => m == 1))
            : 1;
        maxBatchSeqLen = Math.Min(maxBatchSeqLen, _tokenizer.MaxLength);
        var batchSize = inputs.Count;
        var seqLen = maxBatchSeqLen;

        var inputIdsArray = new long[batchSize * seqLen];
        var attentionMaskArray = new long[batchSize * seqLen];
        var tokenTypeIdsArray = new long[batchSize * seqLen];

        for (int b = 0; b < batchSize; b++)
        {
            var seq = inputs[b];
            for (int s = 0; s < seqLen; s++)
            {
                var idx = b * seqLen + s;
                inputIdsArray[idx] = seq.InputIds[s];
                attentionMaskArray[idx] = seq.AttentionMask[s];
                tokenTypeIdsArray[idx] = seq.TokenTypeIds[s];
            }
        }

        var shape = new[] { batchSize, seqLen };
        var inputNames = _session.InputNames;

        var namedInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputNames[0],
                new DenseTensor<long>(inputIdsArray, shape)),
            NamedOnnxValue.CreateFromTensor(inputNames[1],
                new DenseTensor<long>(attentionMaskArray, shape)),
            NamedOnnxValue.CreateFromTensor(inputNames[2],
                new DenseTensor<long>(tokenTypeIdsArray, shape)),
        };

        using var ortOutputs = _session.Run(namedInputs);
        var outputKey = _session.OutputNames[0];
        var outputValue = ortOutputs.First(o => o.Name == outputKey);
        var outputTensor = outputValue.AsTensor<float>();
        var outputArray = outputTensor.ToArray();

        for (int b = 0; b < batchSize; b++)
        {
            var embedding = MeanPoolAndNormalize(
                outputArray.AsSpan(), attentionMaskArray.AsSpan(),
                b, batchSize, seqLen, _dimension);
            results.Add(new Embedding<float>(embedding));
        }

        return Task.FromResult(results);
    }

    private static float[] MeanPoolAndNormalize(
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
}
