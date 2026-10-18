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

    // The memo being edited (null for a new memo). Kept separately from the list selection
    private MemoItem? _editingMemo;

    // The running AI task and the way to stop it
    private Task? _runningTask;
    private CancellationTokenSource? _cancel;
    private bool _isPreparing;
    private bool _isClosing;
    private bool _canClose;

    // Recording
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

    // ---- Prepare AI ----

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

    // ---- Summary, tags, and category ----

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (!CanAsk()) return;
        // (1) Read the memo text
        var body = BodyBox.Text.Trim();

        await RunAsync("Status_Summarizing", async ct =>
        {
            // (2) Pass it to the AI
            var summary = await _ai.AskAsync(AiPrompts.Summarize, body, ct);
            // (3) Show the suggestion
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

    // Read the tags (up to 3 words starting with #) from the first line, and the category from the "Category:" line.
    private static (string Tags, string Category) ReadTagsAndCategory(string answer)
    {
        var lines = answer
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Accept spaces or commas between tags, and make sure every tag starts with #
        var tagWords = lines.FirstOrDefault(line => line.Contains('#'))?
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => "#" + word.TrimStart('#'))
            .Where(word => word.Length > 1)
            .Distinct()
            .Take(3) ?? [];

        var categoryLine = lines.FirstOrDefault(line => line.StartsWith(AiPrompts.CategoryPrefix, StringComparison.OrdinalIgnoreCase));
        var category = Categories.FromDisplayName(categoryLine?[AiPrompts.CategoryPrefix.Length..]);

        return (string.Join(" ", tagWords), category);
    }

    // ---- Recording and transcription ----

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
        // Disable the button until the microphone is ready
        RecordButton.IsEnabled = false;
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio
            });

            // Record as 16 kHz mono WAV (a format the speech model handles well)
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
            // No microphone, or access is not allowed. The user can still type.
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

        // A nearly silent recording can make the speech model invent unrelated text, so check it first
        if (await IsTooQuietAsync(file)) throw new LocalAiException("Error_EmptyTranscription");

        var text = await _ai.TranscribeAsync(file.Path, ct);
        // Append the transcript to the end of the memo (never overwrite it)
        BodyBox.Text = string.IsNullOrWhiteSpace(BodyBox.Text) ? text : $"{BodyBox.Text.TrimEnd()}\r{text}";
        ShowStatus("Status_Transcribed");
    }

    // Treat the recording as "could not hear" if it is shorter than 1 second or even its loudest sound is too quiet.
    // At 16 kHz, 16-bit, mono, one second is 32,000 bytes.
    private static async Task<bool> IsTooQuietAsync(StorageFile file)
    {
        var bytes = await File.ReadAllBytesAsync(file.Path);
        // The audio samples start 8 bytes after the "data" marker
        var dataStart = bytes.AsSpan(0, Math.Min(bytes.Length, 256)).IndexOf("data"u8) + 8;
        if (dataStart < 8 || bytes.Length - dataStart < 32000) return true;

        var peak = 0;
        for (var i = dataStart; i + 1 < bytes.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
        }
        // Only very quiet sound (the maximum is 32,767)
        return peak < 1000;
    }

    private void ReleaseCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _recordingFile = null;
        RecordButton.Content = Strings.GetString("Record_Start");
    }

    // ---- Save, list, and filters ----

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
                // Also read categories that an older version saved as display names
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

    // Show only the memos that match the category and tag filters (no AI involved)
    private void ApplyFilter(MemoItem? selected)
    {
        // Item 0 is "All"
        var categoryIndex = CategoryFilterBox.SelectedIndex - 1;
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

    // ---- Open folders ----

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

    // ---- Shared: busy state, buttons, and error messages ----

    private bool CanAsk()
    {
        if (string.IsNullOrWhiteSpace(BodyBox.Text))
        {
            ShowStatus("Error_EmptyBody");
            return false;
        }
        return true;
    }

    // Run one AI task. Disable the buttons while it runs, and show a short message if it fails.
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
            // Stopped because the window is closing: show nothing
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
            // Show how long it took (used to compare models in Chapter 7)
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

    // ---- Clean up when closing ----

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        UpdateButtons();
        ShowStatus("Status_Closing");

        // Stop any running transcription or summary, wait for it to finish, then release the models
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
        // Preparing (downloading) cannot be stopped, so close without waiting for cleanup

        _canClose = true;
        Close();
    }
}
