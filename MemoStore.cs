using System.Text.Json;
using Windows.Storage;

namespace LocalAiMemoApp;

/// <summary>
/// メモをPC内のJSONファイル（memos.json）に保存・読み込みする係。
/// </summary>
public static class MemoStore
{
    private const string FileName = "memos.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task<IReadOnlyList<MemoItem>> LoadAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
            var json = await FileIO.ReadTextAsync(file);
            return JsonSerializer.Deserialize<List<MemoItem>>(json) ?? [];
        }
        catch (FileNotFoundException)
        {
            // まだ一度も保存していない
            return [];
        }
    }

    public static async Task SaveAsync(IEnumerable<MemoItem> memos)
    {
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            FileName, CreationCollisionOption.ReplaceExisting);
        var json = JsonSerializer.Serialize(memos, Options);
        await FileIO.WriteTextAsync(file, json);
    }
}
