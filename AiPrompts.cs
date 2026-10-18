using Microsoft.Windows.ApplicationModel.Resources;

namespace LocalAiMemoApp;

/// <summary>
/// AIへのお願い（プロンプト）をまとめた場所。
/// 文面は Strings/ja-JP/Prompts.resw にあり、コードを変えずに直せる。
/// </summary>
public static class AiPrompts
{
    private static readonly ResourceLoader Loader =
        new(ResourceLoader.GetDefaultResourceFilePath(), "Prompts");

    public static string Summarize => Loader.GetString("Summarize");

    public static string TagsAndCategory => Loader.GetString("TagsAndCategory");

    // タグ・分類の返事のうち「分類:」の行を見分けるための書き出し。
    public static string CategoryPrefix => Loader.GetString("CategoryPrefix");
}
