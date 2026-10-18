using System.Text.Json.Serialization;

namespace LocalAiMemoApp;

/// <summary>
/// 1件のメモ。JSONファイルに保存する項目を持つ。
/// </summary>
public sealed class MemoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Body { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string Category { get; set; } = Categories.None;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    // 一覧に表示する文字（保存はしない）
    [JsonIgnore]
    public string Title => Body.Split(['\r', '\n']).FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;

    [JsonIgnore]
    public string CategoryName => Categories.DisplayName(Category);
}
