using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace MailClient.Services.Ai;

/// Local inference via ONNX Runtime GenAI. One generation at a time (serialised by a gate).
public sealed class OnnxGenAiService : IAiService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnnxGenAiService(Model model, Tokenizer tokenizer, AiBackend backend, string modelName)
    {
        _model = model;
        _tokenizer = tokenizer;
        Backend = backend;
        ModelName = modelName;
        IsReady = true;
    }

    public bool IsReady { get; }

    public AiBackend Backend { get; }

    public string ModelName { get; }

    /// Loads the model. Throws OnnxRuntimeGenAIException on an incompatible / broken model.
    public static OnnxGenAiService Create(string modelDir, AiBackend backend, string modelName)
    {
        var model = new Model(modelDir);
        var tokenizer = new Tokenizer(model);
        LoggingService.Info("OnnxGenAiService", $"loaded {modelName} ({backend}) from {modelDir}");
        return new OnnxGenAiService(model, tokenizer, backend, modelName);
    }

    private static string BuildChat(AiPrompt p) =>
        (string.IsNullOrEmpty(p.System) ? string.Empty : $"<|system|>\n{p.System}<|end|>\n") +
        $"<|user|>\n{p.User}<|end|>\n<|assistant|>\n";

    public async IAsyncEnumerable<string> StreamAsync(AiPrompt prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var channel = Channel.CreateUnbounded<string>();

            _ = Task.Run(() =>
            {
                try
                {
                    using var sequences = _tokenizer.Encode(BuildChat(prompt));
                    var promptLength = sequences[0].Length;

                    using var generatorParams = new GeneratorParams(_model);
                    generatorParams.SetSearchOption("max_length", promptLength + prompt.MaxTokens);
                    generatorParams.SetSearchOption("temperature", prompt.Temperature);
                    generatorParams.SetSearchOption("do_sample", prompt.Temperature > 0.01f);

                    using var generator = new Generator(_model, generatorParams);
                    generator.AppendTokenSequences(sequences);
                    using var stream = _tokenizer.CreateStream();

                    while (!generator.IsDone() && !ct.IsCancellationRequested)
                    {
                        generator.GenerateNextToken();

                        var next = generator.GetNextTokens();
                        if (next.Length == 0)
                        {
                            continue;
                        }

                        var piece = stream.Decode(next[0]);
                        if (!string.IsNullOrEmpty(piece))
                        {
                            channel.Writer.TryWrite(piece);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("OnnxGenAiService.generate", ex);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            await foreach (var piece in channel.Reader.ReadAllAsync(ct))
            {
                yield return piece;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CompleteAsync(AiPrompt prompt, CancellationToken ct)
    {
        var builder = new StringBuilder();
        await foreach (var piece in StreamAsync(prompt, ct))
        {
            builder.Append(piece);
        }

        return builder.ToString().Trim();
    }

    public async Task<T?> CompleteJsonAsync<T>(AiPrompt prompt, CancellationToken ct)
    {
        var text = await CompleteAsync(prompt, ct);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], JsonOpts);
        }
        catch (JsonException ex)
        {
            LoggingService.Warn("OnnxGenAiService.CompleteJsonAsync", ex);
            return default;
        }
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
        _model.Dispose();
        _gate.Dispose();
    }
}
