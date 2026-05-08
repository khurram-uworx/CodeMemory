using Microsoft.Extensions.AI;
using System.Numerics.Tensors;
using System.Text.RegularExpressions;

namespace CodeMemory.Services.Embedding;

public sealed partial class NgramEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    const int Dimension = 1536;
    const int HashesPerNgram = 4;
    static readonly int[] NgramLengths = [2, 3, 4];

    static readonly EmbeddingGeneratorMetadata Metadata = new("ngram-v1", null, null);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
            results.Add(new Embedding<float>(generate(text)));
        return Task.FromResult(results);
    }

    static float[] generate(string text)
    {
        var cleaned = WhitespaceRegex().Replace(text, " ");
        var embedding = new float[Dimension];

        foreach (var len in NgramLengths)
        {
            for (int i = 0; i <= cleaned.Length - len; i++)
            {
                var hash = hashNgram(cleaned, i, len);
                for (int h = 0; h < HashesPerNgram; h++)
                {
                    var combined = HashCode.Combine(hash, h);
                    var bucket = Math.Abs(combined) % Dimension;
                    embedding[bucket] += (combined & 1) == 0 ? 1f : -1f;
                }
            }
        }

        var norm = MathF.Sqrt(TensorPrimitives.SumOfSquares(embedding));
        if (norm > 0)
            TensorPrimitives.Divide(embedding, norm, embedding);

        return embedding;
    }

    static int hashNgram(string text, int start, int length)
    {
        var hash = length;
        for (int i = 0; i < length; i++)
            hash = hash * 31 + text[start + i];
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata :
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
