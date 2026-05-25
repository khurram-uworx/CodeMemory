using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace CodeMemory.AspNet.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMemoryOnnxEmbeddingGenerator(
        this IServiceCollection services,
        string modelPath,
        string vocabPath)
    {
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    "ONNX model not found. Run download-models.ps1 or use OnnxModelDownloader.", modelPath);
            if (!File.Exists(vocabPath))
                throw new FileNotFoundException(
                    "Vocabulary file not found. Run download-models.ps1 or use OnnxModelDownloader.", vocabPath);

            return new BertOnnxEmbeddingGenerator(modelPath, vocabPath);
        });

        return services;
    }

    public static IServiceCollection AddCodeMemorySKOnnxEmbeddingGenerator(
        this IServiceCollection services,
        string modelPath,
        string vocabPath)
    {
        services.AddBertOnnxEmbeddingGenerator(modelPath, vocabPath);

        return services;
    }

    public static IServiceCollection AddCodeMemoryOllamaEmbeddingGenerator(
        this IServiceCollection services,
        Uri ollamaEndpoint,
        string modelName)
    {
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            return new OllamaApiClient(ollamaEndpoint, modelName);
        });

        return services;
    }
}
