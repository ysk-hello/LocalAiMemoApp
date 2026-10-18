using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace LocalAiMemoApp;

public sealed partial class MainWindow : Window
{
    private static readonly ResourceLoader Strings = new();

    private readonly LocalAiService _ai = new();
    private readonly List<MemoItem> _allMemos = [];
    private readonly ObservableCollection<MemoItem> _visibleMemos = [];

    // 編集中のメモ（新しいメモのときは null）。一覧の選択とは別に覚えておく
    private MemoItem? _editingMemo;

    // 実行中のAI処理と、それを止めるための仕組み
    private Task? _runningTask;
    private CancellationTokenSource? _cancel;
    private bool _isPreparing;
    private bool _isClosing;
    private bool _canClose;

    // 録音
    private MediaCapture? _capture;
    private StorageFile? _recordingFile;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();
        Title = Strings.GetString("AppTitle");
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1160 * scale), (int)(800 * scale)));
        AppWindow.Closing += Window_Closing;

        CategoryFilterBox.Items.Add(Strings.GetString("Filter_All"));
        foreach (var key in Categories.All)
        {
            CategoryFilterBox.Items.Add(Categories.DisplayName(key));
            CategoryBox.Items.Add(Categories.DisplayName(key));
        }
        CategoryFilterBox.SelectedIndex = 0;
        CategoryBox.SelectedIndex = 0;
        MemoList.ItemsSource = _visibleMemos;

        RecordButton.Content = Strings.GetString("Record_Start");
        ShowStatus("Status_Welcome");
        UpdateButtons();
        _ = LoadMemosAsync();
    }

    // ---- AIの準備 ----

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        _isPreparing = true;
        PrepareProgress.Value = 0;
        PrepareProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(value => PrepareProgress.Value = value);

        await RunAsync("Status_Preparing", async _ =>
        {
            await _ai.PrepareAsync(progress);
            ShowStatus("Status_Ready");
        });

        _isPreparing = false;
        PrepareProgress.Visibility = Visibility.Collapsed;
        PrepareNote.Visibility = _ai.IsReady ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons();
    }

    // ---- 要約・タグと分類 ----

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAsk()) return;
        // ① 本文を読む
        var body = BodyBox.Text.Trim();

        await RunAsync("Status_Summarizing", async ct =>
        {
            // ② AIへ渡す
            var summary = await _ai.AskAsync(AiPrompts.Summarize, body, ct);
            // ③ 候補を表示する
            SummaryBox.Text = summary;
            ShowStatus("Status_Summarized");
        });
    }

    private async void Tag_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAsk()) return;
        var body = BodyBox.Text.Trim();

        await RunAsync("Status_Tagging", async ct =>
        {
            var answer = await _ai.AskAsync(AiPrompts.TagsAndCategory, body, ct);
            var (tags, category) = ReadTagsAndCategory(answer);
            TagsBox.Text = tags;
            CategoryBox.SelectedIndex = Array.IndexOf(Categories.All, category);
            ShowStatus("Status_Tagged");
        });
    }

    // 返事の1行目からタグ（#で始まる語を最大3個）、「分類:」の行から分類を読み取る。
    private static (string Tags, string Category) ReadTagsAndCategory(string answer)
    {
        var lines = answer.Replace('：', ':')
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 区切りが空白でも「、」でも読めるようにし、# を付けそろえる
        var tagWords = lines.FirstOrDefault(line => line.Contains('#'))?
            .Split([' ', '　', ',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => "#" + word.TrimStart('#'))
            .Where(word => word.Length > 1)
            .Distinct()
            .Take(3) ?? [];

        var categoryLine = lines.FirstOrDefault(line => line.StartsWith(AiPrompts.CategoryPrefix));
        var category = Categories.FromDisplayName(categoryLine?[AiPrompts.CategoryPrefix.Length..]);

        return (string.Join(" ", tagWords), category);
    }

    // ---- 録音と文字起こし ----

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null)
        {
            await StartRecordingAsync();
        }
        else
        {
            await RunAsync("Status_Transcribing", StopAndTranscribeAsync);
        }
    }

    private async Task StartRecordingAsync()
    {
        // マイクの準備が終わるまでは、もう一度押せないようにする
        RecordButton.IsEnabled = false;
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            });

            // 16kHz・モノラルのWAV形式で録音する（音声モデルが扱いやすい形式）
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);
            _recordingFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                "recording.wav", CreationCollisionOption.ReplaceExisting);
            await _capture.StartRecordToStorageFileAsync(profile, _recordingFile);

            RecordButton.Content = Strings.GetString("Record_Stop");
            ShowStatus("Status_Recording");
        }
        catch (Exception)
        {
            // マイクが無い・使用が許可されていない場合。手入力は続けられる。
            ReleaseCapture();
            ShowStatus("Error_Microphone");
        }
        UpdateButtons();
    }

    private async Task StopAndTranscribeAsync(CancellationToken ct)
    {
        var file = _recordingFile!;
        try
        {
            await _capture!.StopRecordAsync();
        }
        finally
        {
            ReleaseCapture();
        }

        // ほとんど無音の録音を渡すと、音声モデルが無関係な文を作ることがあるため、先に確かめる
        if (await IsTooQuietAsync(file)) throw new LocalAiException("Error_EmptyTranscription");

        var text = await _ai.TranscribeAsync(file.Path, ct);
        // 文字起こしの結果は、本文の末尾に追加する（上書きしない）
        BodyBox.Text = string.IsNullOrWhiteSpace(BodyBox.Text) ? text : $"{BodyBox.Text.TrimEnd()}\r{text}";
        ShowStatus("Status_Transcribed");
    }

    // 録音が1秒未満、または一番大きな音でも小さすぎるときは「聞き取れなかった」とみなす。
    // 16kHz・16ビット・モノラルなので、1秒は32,000バイト。
    private static async Task<bool> IsTooQuietAsync(StorageFile file)
    {
        var bytes = await File.ReadAllBytesAsync(file.Path);
        // 音の中身は "data" という目印の8バイト後から始まる
        var dataStart = bytes.AsSpan(0, Math.Min(bytes.Length, 256)).IndexOf("data"u8) + 8;
        if (dataStart < 8 || bytes.Length - dataStart < 32000) return true;

        var peak = 0;
        for (var i = dataStart; i + 1 < bytes.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
        }
        return peak < 1000; // 最大値 32,767 のうち、ごく小さな音しかない
    }

    private void ReleaseCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _recordingFile = null;
        RecordButton.Content = Strings.GetString("Record_Start");
    }

    // ---- 保存・一覧・絞り込み ----

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            ShowStatus("Error_EmptyBody");
            return;
        }

        var memo = _editingMemo;
        if (memo is null)
        {
            memo = new MemoItem();
            _allMemos.Insert(0, memo);
            _editingMemo = memo;
        }
        memo.Body = BodyBox.Text.Trim();
        memo.Summary = SummaryBox.Text.Trim();
        memo.Tags = TagsBox.Text.Trim();
        memo.Category = Categories.All[Math.Max(CategoryBox.SelectedIndex, 0)];
        memo.UpdatedAt = DateTimeOffset.Now;

        try
        {
            await MemoStore.SaveAsync(_allMemos);
            ApplyFilter(memo);
            ShowStatus("Status_Saved");
        }
        catch (Exception)
        {
            ShowStatus("Error_Save");
        }
    }

    private async Task LoadMemosAsync()
    {
        try
        {
            foreach (var memo in await MemoStore.LoadAsync())
            {
                // 以前の版で日本語のまま保存した分類も読めるようにする
                if (!Categories.All.Contains(memo.Category))
                {
                    memo.Category = Categories.FromDisplayName(memo.Category);
                }
                _allMemos.Add(memo);
            }
            ApplyFilter(null);
        }
        catch (Exception)
        {
            ShowStatus("Error_Load");
        }
    }

    private void Filter_Changed(object sender, object e) => ApplyFilter(_editingMemo);

    // 分類とタグの条件に合うメモだけを一覧に並べる（AIは使わない）
    private void ApplyFilter(MemoItem? selected)
    {
        var categoryIndex = CategoryFilterBox.SelectedIndex - 1; // 0番目は「すべて」
        var tag = TagFilterBox.Text.Trim().TrimStart('#');

        var matches = _allMemos.Where(memo =>
            (categoryIndex < 0 || memo.Category == Categories.All[categoryIndex]) &&
            (tag.Length == 0 || memo.Tags.Contains(tag, StringComparison.CurrentCultureIgnoreCase)));

        _visibleMemos.Clear();
        foreach (var memo in matches) _visibleMemos.Add(memo);
        MemoList.SelectedItem = selected is not null && _visibleMemos.Contains(selected) ? selected : null;
    }

    private void MemoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MemoList.SelectedItem is not MemoItem memo) return;
        _editingMemo = memo;
        BodyBox.Text = memo.Body;
        SummaryBox.Text = memo.Summary;
        TagsBox.Text = memo.Tags;
        CategoryBox.SelectedIndex = Math.Max(Array.IndexOf(Categories.All, memo.Category), 0);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _editingMemo = null;
        MemoList.SelectedItem = null;
        BodyBox.Text = SummaryBox.Text = TagsBox.Text = string.Empty;
        CategoryBox.SelectedIndex = 0;
        ShowStatus("Status_New");
        BodyBox.Focus(FocusState.Programmatic);
    }

    // ---- 保存先を開く ----

    private void OpenModelFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".LocalAiMemoApp"));

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(ApplicationData.Current.LocalFolder.Path);

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            ShowStatus("Error_FolderNotFound");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ---- 共通：実行中の表示・ボタンの切り替え・エラー表示 ----

    private bool CanAsk()
    {
        if (string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            ShowStatus("Error_EmptyBody");
            return false;
        }
        return true;
    }

    // AIの処理を1つ実行する。実行中はボタンを押せなくし、失敗したら短いメッセージを出す。
    private async Task RunAsync(string statusKey, Func<CancellationToken, Task> action)
    {
        _cancel = new CancellationTokenSource();
        ShowStatus(statusKey);
        ElapsedLabel.Text = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _runningTask = action(_cancel.Token);
            UpdateButtons();
            await _runningTask;
        }
        catch (OperationCanceledException)
        {
            // 閉じる操作で止めた場合は何も表示しない
        }
        catch (LocalAiException ex)
        {
            ShowStatus(ex.MessageKey);
        }
        catch (Exception)
        {
            ShowStatus(_ai.IsReady ? "Error_TryAgain" : "Error_Prepare");
        }
        finally
        {
            // かかった時間を表示する（第7章でモデルを比べるときに使う）
            ElapsedLabel.Text = string.Format(Strings.GetString("Elapsed_Format"), stopwatch.Elapsed.TotalSeconds);
            _runningTask = null;
            _cancel.Dispose();
            _cancel = null;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var busy = _runningTask is not null || _isClosing;
        var recording = _capture is not null;
        var ready = _ai.IsReady;

        PrepareButton.IsEnabled = !busy && !ready;
        RecordButton.IsEnabled = !busy && ready;
        SummarizeButton.IsEnabled = TagButton.IsEnabled = !busy && !recording && ready;
        NewButton.IsEnabled = SaveButton.IsEnabled = MemoList.IsEnabled = !busy && !recording;
        BodyBox.IsReadOnly = busy;
        AiButtonsNote.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowStatus(string key) => StatusLabel.Text = Strings.GetString(key);

    // ---- 閉じる時の後片付け ----

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        UpdateButtons();
        ShowStatus("Status_Closing");

        // 実行中の文字起こし・要約を止め、終わるのを待ってからモデルを解放する
        _cancel?.Cancel();
        ReleaseCapture();
        if (!_isPreparing)
        {
            if (_runningTask is not null)
            {
                try { await _runningTask; } catch (Exception) { }
            }
            try { await _ai.DisposeAsync(); } catch (Exception) { }
        }
        // 準備中（ダウンロード中）は止められないため、片付けを待たずに閉じる

        _canClose = true;
        Close();
    }
}
