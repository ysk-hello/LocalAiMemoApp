using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAiMemoApp;

/// <summary>
/// Foundry Local を使って、文字起こしと文章整理を行う係。
/// 役割は「初期化」「モデル準備」「文字起こし」「文章整理」「解放」の5つ。
/// 画面（WinUI）の部品には触らない。
/// </summary>
public sealed class LocalAiService : IAsyncDisposable
{
    public const string TextModelAlias = "phi-4-mini";
    public const string AudioModelAlias = "whisper-medium";
    public const int MaxBodyLength = 4000;

    // AI への依頼を1つずつ順番に処理するための「入口の鍵」。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FoundryLocalManager? _manager;
    private IModel? _textModel;
    private IModel? _audioModel;

    public bool IsReady { get; private set; }

    // ① 初期化 と ② モデル準備：初回はダウンロードし、2回目以降は保存済みのモデルを読み込む。
    public async Task PrepareAsync(IProgress<double> progress)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsReady) return;

            // 準備の処理は重いので、画面を固めないよう別のスレッドで動かす。
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

                // 2つのモデルを順に取得する。進み具合は 0〜100 にまとめて知らせる。
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

    // ③ 文字起こし：音声ファイルを渡し、文字にして返す。
    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var audioModel = GetReadyModel(_audioModel);
            return await Task.Run(async () =>
            {
                var client = await audioModel.GetAudioClientAsync(ct);
                client.Settings.Language = "ja";
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

    // ④ 文章整理：AIへのお願い（instruction）と本文を渡し、返事を受け取る。
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

                // 毎回新しいメッセージを作る。前のメモの内容を会話として覚えることはない。
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

    // ⑤ 解放：実行中の処理が終わるのを待ってから、モデルとFoundry Localを片付ける。
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
/// 画面に出すメッセージを、リソース（.resw）のキーで持つ例外。
/// </summary>
public sealed class LocalAiException(string messageKey) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;
}
