using Microsoft.Windows.ApplicationModel.Resources;

namespace LocalAiMemoApp;

/// <summary>
/// Memo categories. The key is saved to the file; the display name comes from the .resw file.
/// </summary>
public static class Categories
{
    public const string None = "None";
    public static readonly string[] All = [None, "Work", "Personal", "Idea", "Learning"];

    private static readonly ResourceLoader Loader = new();

    public static string DisplayName(string key) => Loader.GetString($"Category_{key}");

    // Finds the key for a display name (e.g. "Work"), ignoring case. Falls back to "Uncategorized".
    public static string FromDisplayName(string? name) =>
        All.FirstOrDefault(key => string.Equals(
            DisplayName(key), name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? None;
}
