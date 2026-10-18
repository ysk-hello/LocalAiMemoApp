using Microsoft.Windows.ApplicationModel.Resources;

namespace LocalAiMemoApp;

/// <summary>
/// The place that holds the requests (prompts) sent to the AI.
/// The text lives in Strings/en-US/Prompts.resw, so you can edit it without changing the code.
/// </summary>
public static class AiPrompts
{
    private static readonly ResourceLoader Loader =
        new(ResourceLoader.GetDefaultResourceFilePath(), "Prompts");

    public static string Summarize => Loader.GetString("Summarize");

    public static string TagsAndCategory => Loader.GetString("TagsAndCategory");

    // The prefix used to find the "Category:" line in the tag-and-category answer.
    public static string CategoryPrefix => Loader.GetString("CategoryPrefix");
}
