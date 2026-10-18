using Microsoft.Windows.ApplicationModel.Resources;

namespace LocalAiMemoApp;

/// <summary>
/// メモの分類。保存するのは英語のキー、画面に出すのは .resw の表示名。
/// </summary>
public static class Categories
{
    public const string None = "None";
    public static readonly string[] All = [None, "Work", "Personal", "Idea", "Learning"];

    private static readonly ResourceLoader Loader = new();

    public static string DisplayName(string key) => Loader.GetString($"Category_{key}");

    // 表示名（例：「仕事」）からキーを探す。見つからなければ「未分類」。
    public static string FromDisplayName(string? name) =>
        All.FirstOrDefault(key => DisplayName(key) == name?.Trim()) ?? None;
}
