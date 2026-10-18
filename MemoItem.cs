using System.Text.Json.Serialization;

namespace LocalAiMemoApp;

/// <summary>
/// One memo. Holds the fields saved to the JSON file.
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

    // Text shown in the list (not saved)
    [JsonIgnore]
    public string Title => Body.Split(['\r', '\n']).FirstOrDefault(line => line.Trim().Length > 0)?.Trim() ?? string.Empty;

    [JsonIgnore]
    public string CategoryName => Categories.DisplayName(Category);
}
