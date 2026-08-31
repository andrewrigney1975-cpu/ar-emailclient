using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailClient.Services.Ai;

public sealed record AiModelInfo(
    string Id, string DisplayName, string Repo, string SubPath, string ApproxSize, AiBackend Backend);

public sealed record AiDownloadProgress(string File, long DoneBytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)DoneBytes / TotalBytes : 0;
}

/// Locates, downloads and verifies ONNX model folders under
/// %LocalAppData%\WinUI3Mail\models\&lt;id&gt;\. Models are pulled from Hugging Face on first
/// opt-in and never bundled.
public static class AiModelManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string ModelsRoot { get; } = AppPaths.InData("models");

    public static string Dir(string id) => Path.Combine(ModelsRoot, id);

    public static bool IsInstalled(string id) =>
        File.Exists(Path.Combine(Dir(id), ".complete")) &&
        File.Exists(Path.Combine(Dir(id), "genai_config.json"));

    public static IReadOnlyList<AiModelInfo> Catalog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ai-models.json");
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<AiModelInfo>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            }) ?? new List<AiModelInfo>();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AiModelManager.Catalog", ex);
            return Array.Empty<AiModelInfo>();
        }
    }

    public static AiModelInfo? Find(string id) => Catalog().FirstOrDefault(m => m.Id == id);

    public static void Delete(string id)
    {
        try
        {
            if (Directory.Exists(Dir(id)))
            {
                Directory.Delete(Dir(id), recursive: true);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AiModelManager.Delete", ex);
        }
    }

    private sealed record HfTreeItem(string Type, string Path, long Size, HfLfs? Lfs);
    private sealed record HfLfs(string Oid, long Size);

    /// Downloads every file under the model's subfolder, verifying LFS files by SHA-256.
    public static async Task DownloadAsync(AiModelInfo model, IProgress<AiDownloadProgress>? progress, CancellationToken ct)
    {
        var targetDir = Dir(model.Id);
        Directory.CreateDirectory(targetDir);

        var treeUrl = $"https://huggingface.co/api/models/{model.Repo}/tree/main/{model.SubPath}?recursive=true";
        var tree = await Http.GetFromJsonAsync<List<HfTreeItem>>(treeUrl, JsonOpts, ct)
                   ?? throw new InvalidOperationException("Empty model file listing from Hugging Face.");

        var files = tree.Where(t => string.Equals(t.Type, "file", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No files at {model.Repo}/{model.SubPath} - check ai-models.json.");
        }

        var total = files.Sum(f => f.Size);
        long done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = file.Path[model.SubPath.Length..].TrimStart('/');
            var localPath = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            if (File.Exists(localPath) && new FileInfo(localPath).Length == file.Size)
            {
                done += file.Size;
                progress?.Report(new AiDownloadProgress(relative, done, total));
                continue;
            }

            var url = $"https://huggingface.co/{model.Repo}/resolve/main/{file.Path}";
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var netStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = File.Create(localPath);

                var buffer = new byte[1 << 20];
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new AiDownloadProgress(relative, done, total));
                }
            }

            if (file.Lfs is { Oid.Length: > 0 } lfs && !await VerifyAsync(localPath, lfs.Oid, ct))
            {
                File.Delete(localPath);
                throw new InvalidOperationException($"Checksum mismatch for {relative}.");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(targetDir, ".complete"), DateTime.UtcNow.ToString("O"), ct);
    }

    private static async Task<bool> VerifyAsync(string path, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
