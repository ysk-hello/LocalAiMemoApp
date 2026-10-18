using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAiMemoApp;

/// <summary>
/// The helper that uses Foundry Local to transcribe speech and organize text.
/// It has five jobs: initialize, prepare models, transcribe, organize text, and release.
/// It never touches the UI (WinUI) controls.
/// </summary>
public sealed class LocalAiService : IAsyncDisposable
{
    public const string TextModelAlias = "phi-4-mini";
    public const string AudioModelAlias = "whisper-medium";
    public const int MaxBodyLength = 4000;

    // A "gate" so that requests to the AI are handled one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FoundryLocalManager? _manager;
    private IModel? _textModel;
    private IModel? _audioModel;

    public bool IsReady { get; private set; }

    // (1) Initialize and (2) prepare models: download them the first time, then load the saved models.
    public async Task PrepareAsync(IProgress<double> progress)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsReady) return;

            // Preparing is heavy, so run it on another thread to keep the UI responsive.
            await Task.Run(async () =>
            {
                if (_manager is null)
                {
                    await FoundryLocalManager.CreateAsync(
                        new Configuration { AppName = "LocalAiMemoApp" }, NullLogger.Instance);
                    _manager = FoundryLocalManager.Instance;
                }

                var catalog = await _manager.GetCatalogAsync();
                _textModel = await catalog.GetModelAsync(TextModelAlias)
                    ?? throw new LocalAiException("Error_ModelNotFound");
                _audioModel = await catalog.GetModelAsync(AudioModelAlias)
                    ?? throw new LocalAiException("Error_ModelNotFound");

                // Get the two models in turn. Progress is reported as a single 0-100 value.
                await DownloadIfNeededAsync(_audioModel, value => progress.Report(value * 0.2));
                await DownloadIfNeededAsync(_textModel, value => progress.Report(20 + value * 0.8));

                await _audioModel.LoadAsync();
                await _textModel.LoadAsync();
            });
            IsReady = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task DownloadIfNeededAsync(IModel model, Action<float> onProgress)
    {
        if (await model.IsCachedAsync())
        {
            onProgress(100);
            return;
        }
        await model.DownloadAsync(onProgress);
    }

    // (3) Transcribe: pass an audio file and return the text.
    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var audioModel = GetReadyModel(_audioModel);
            return await Task.Run(async () =>
            {
                var client = await audioModel.GetAudioClientAsync(ct);
                client.Settings.Language = "en";
                client.Settings.Temperature = 0f;
                var response = await client.TranscribeAudioAsync(audioFilePath, ct);
                var text = response.Text?.Trim();
                return string.IsNullOrWhiteSpace(text)
                    ? throw new LocalAiException("Error_EmptyTranscription")
                    : text;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // (4) Organize text: pass the request (instruction) and the memo text, and get the answer.
    public async Task<string> AskAsync(string instruction, string body, CancellationToken ct)
    {
        if (body.Length > MaxBodyLength) throw new LocalAiException("Error_BodyTooLong");

        await _gate.WaitAsync(ct);
        try
        {
            var textModel = GetReadyModel(_textModel);
            return await Task.Run(async () =>
            {
                var client = await textModel.GetChatClientAsync(ct);
                client.Settings.Temperature = 0.1f;
                client.Settings.MaxTokens = 256;

                // Create new messages every time. The AI does not remember earlier memos as a conversation.
                List<ChatMessage> messages =
                [
                    new ChatMessage { Role = "system", Content = instruction },
                    new ChatMessage { Role = "user", Content = body }
                ];
                var response = await client.CompleteChatAsync(messages, ct);
                var text = response.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
                return !response.Successful || string.IsNullOrWhiteSpace(text)
                    ? throw new LocalAiException("Error_EmptyAnswer")
                    : text;
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IModel GetReadyModel(IModel? model) =>
        IsReady && model is not null ? model : throw new LocalAiException("Error_NotReady");

    // (5) Release: wait for running work to finish, then clean up the models and Foundry Local.
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            IsReady = false;
            await Task.Run(async () =>
            {
                if (_textModel is not null) await _textModel.UnloadAsync();
                if (_audioModel is not null) await _audioModel.UnloadAsync();
            });
        }
        finally
        {
            _textModel = null;
            _audioModel = null;
            _manager?.Dispose();
            _manager = null;
            _gate.Release();
        }
    }
}

/// <summary>
/// An exception that carries the resource (.resw) key of the message to show on screen.
/// </summary>
public sealed class LocalAiException(string messageKey) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;
}
