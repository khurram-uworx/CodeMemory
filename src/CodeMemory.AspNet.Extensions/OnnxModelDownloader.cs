namespace CodeMemory.AspNet.Extensions;

public sealed class OnnxModelDownloader
{
    private readonly HttpClient _http;

    public OnnxModelDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string DefaultModelDirectory =>
        Path.Combine(AppContext.BaseDirectory, "models", "bge-micro-v2");

    public string DefaultModelPath =>
        Path.Combine(DefaultModelDirectory, "model.onnx");

    public string DefaultVocabPath =>
        Path.Combine(DefaultModelDirectory, "vocab.txt");

    public bool IsModelDownloaded =>
        File.Exists(DefaultModelPath) && File.Exists(DefaultVocabPath);

    public async Task EnsureModelAsync(
        string? modelDir = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dir = modelDir ?? DefaultModelDirectory;
        Directory.CreateDirectory(dir);

        var modelPath = Path.Combine(dir, "model.onnx");
        var vocabPath = Path.Combine(dir, "vocab.txt");

        if (File.Exists(modelPath) && File.Exists(vocabPath))
            return;

        var modelUrl = "https://huggingface.co/TaylorAI/bge-micro-v2/resolve/main/onnx/model.onnx";
        var vocabUrl = "https://huggingface.co/TaylorAI/bge-micro-v2/resolve/main/vocab.txt";

        await DownloadFileAsync(modelUrl, modelPath, progress, ct);
        await DownloadFileAsync(vocabUrl, vocabPath, null, ct);
    }

    private async Task DownloadFileAsync(
        string url, string path,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        if (totalBytes > 0)
        {
            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                bytesRead += read;
                progress?.Report((double)bytesRead / totalBytes);
            }
        }
        else
        {
            await contentStream.CopyToAsync(fileStream, ct);
        }
    }
}
