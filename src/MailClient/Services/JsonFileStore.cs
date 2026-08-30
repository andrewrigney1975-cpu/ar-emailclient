using System.Text.Json;

namespace MailClient.Services;

/// Load / cache / save plumbing for the app's local JSON-backed stores (ported from the file
/// explorer project). Each store owns its file name, default value and domain methods.
public sealed class JsonFileStore<T>
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Func<T> _createDefault;
    private T? _cache;

    public JsonFileStore(string fileName, Func<T> createDefault)
    {
        _filePath = AppPaths.InData(fileName);
        _createDefault = createDefault;
    }

    public T Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(_filePath));
                if (loaded is not null)
                {
                    return _cache = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.Warn($"JsonFileStore<{typeof(T).Name}>.Load", ex);
        }

        return _cache = _createDefault();
    }

    public void Save(T value)
    {
        _cache = value;
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(value, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.Warn($"JsonFileStore<{typeof(T).Name}>.Save", ex);
        }
    }
}
