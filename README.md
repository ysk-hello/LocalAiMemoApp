# Local AI Memo

A memo app that saves your notes on your PC and uses Foundry Local to transcribe speech, summarize text, and suggest tags and a category.

## Project structure

| File | Role |
|---|---|
| `MainWindow.xaml` | Layout of the UI controls |
| `MainWindow.xaml.cs` | Button handlers (input → AI call → show suggestion), recording, saving, filtering |
| `LocalAiService.cs` | The Foundry Local helper (initialize, prepare models, transcribe, organize text, release) |
| `AiPrompts.cs` / `Strings/en-US/Prompts.resw` | The requests (prompts) sent to the AI |
| `MemoStore.cs` / `MemoItem.cs` | Saving memos as JSON, and the data for one memo |
| `Categories.cs` | Category keys and display names |
| `Strings/en-US/Resources.resw` | UI text (referenced with `x:Uid`) |

## Requirements

- Windows 11 24H2 (build 26100) or later, x64 or ARM64
- .NET 10 SDK, and Visual Studio (WinUI application development workload) or the winapp CLI
- An internet connection and a few GB of free space for the first model download
- A microphone, if you want to use voice input

Package: `Microsoft.AI.Foundry.Local` 2.1.0. Summaries and transcription use `GetChatClientAsync` / `GetAudioClientAsync`, so the build shows two CS0618 (obsolete) warnings. This is expected, and the app works as is.

## Run

In Visual Studio, run the "LocalAiMemoApp (Package)" profile. From a terminal:

```powershell
dotnet run -p:Platform=x64
```

On an ARM64 PC, replace `x64` with `ARM64`.

## How to use

1. Click "Prepare AI". The first time, the app downloads `phi-4-mini` (text) and `whisper-medium` (speech).
2. Type a memo, or click "Start recording" → "Stop and transcribe" to add what you said to the end of the memo.
3. Click "Summarize" or "Suggest tags and category" to create suggestions. Check and edit them, then click "Save".
4. Use "Filter by category" and "Filter by tag" on the left to filter the list.

Memos are saved as `memos.json` in the app's local data folder. Nothing is saved automatically.
AI output is only a suggestion. Check dates, numbers, and names against the original text.

The English UI is on the `en` branch. The `main` branch has the Japanese UI.
