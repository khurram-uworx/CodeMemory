using System.Reflection;

namespace CodeMemory.AspNet.Extensions;

static class ServiceCollectionExtensions
{
    public static void LoadOnnxExtension(this IServiceCollection services,
        string modelPath, string vocabPath)
    {
        try
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "CodeMemory.AspNet.Extensions.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "CodeMemory.AspNet.Extensions.dll not found at expected path. " +
                    "In Docker: ensure docker-compose.yml builds with --build. " +
                    "Local: run 'dotnet publish src/CodeMemory.AspNet.Extensions' first.",
                    assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            var type = assembly.GetType("CodeMemory.AspNet.Extensions.ServiceCollectionExtensions")
                ?? throw new InvalidOperationException("Type ServiceCollectionExtensions not found");
            var method = type.GetMethod("AddCodeMemoryOnnxEmbeddingGenerator",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [typeof(IServiceCollection), typeof(string), typeof(string)],
                null)
                ?? throw new InvalidOperationException("Method AddCodeMemoryOnnxEmbeddingGenerator not found");

            method.Invoke(null, [services, modelPath, vocabPath]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to load ONNX embedding extension (CodeMemory.AspNet.Extensions). " +
                "Build with: dotnet publish src/CodeMemory.AspNet.Extensions " +
                "or use Embedding:Provider=ngram for zero-dependency mode.", ex);
        }
    }

    public static void LoadSKOnnxExtension(this IServiceCollection services,
        string modelPath, string vocabPath)
    {
        try
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "CodeMemory.AspNet.Extensions.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "CodeMemory.AspNet.Extensions.dll not found at expected path. " +
                    "In Docker: ensure docker-compose.yml builds with --build. " +
                    "Local: run 'dotnet publish src/CodeMemory.AspNet.Extensions' first.",
                    assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            var type = assembly.GetType("CodeMemory.AspNet.Extensions.ServiceCollectionExtensions")
                ?? throw new InvalidOperationException("Type ServiceCollectionExtensions not found");
            var method = type.GetMethod("AddCodeMemorySKOnnxEmbeddingGenerator",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [typeof(IServiceCollection), typeof(string), typeof(string)],
                null)
                ?? throw new InvalidOperationException("Method AddCodeMemorySKOnnxEmbeddingGenerator not found");

            method.Invoke(null, [services, modelPath, vocabPath]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to load SK ONNX embedding extension (CodeMemory.AspNet.Extensions). " +
                "Build with: dotnet publish src/CodeMemory.AspNet.Extensions " +
                "or use Embedding:Provider=ngram for zero-dependency mode.", ex);
        }
    }

    public static void LoadOllamaExtension(this IServiceCollection services,
        Uri endpoint, string modelName)
    {
        try
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "CodeMemory.AspNet.Extensions.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "CodeMemory.AspNet.Extensions.dll not found at expected path. " +
                    "In Docker: ensure docker-compose.yml builds with --build. " +
                    "Local: run 'dotnet publish src/CodeMemory.AspNet.Extensions' first.",
                    assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            var type = assembly.GetType("CodeMemory.AspNet.Extensions.ServiceCollectionExtensions")
                ?? throw new InvalidOperationException("Type ServiceCollectionExtensions not found");
            var method = type.GetMethod("AddCodeMemoryOllamaEmbeddingGenerator",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [typeof(IServiceCollection), typeof(Uri), typeof(string)],
                null)
                ?? throw new InvalidOperationException("Method AddCodeMemoryOllamaEmbeddingGenerator not found");

            method.Invoke(null, [services, endpoint, modelName]);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to load Ollama embedding extension (CodeMemory.AspNet.Extensions). " +
                "Build with: dotnet publish src/CodeMemory.AspNet.Extensions " +
                "or use Embedding:Provider=ngram for zero-dependency mode.", ex);
        }
    }
}
