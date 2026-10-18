# ローカルAIメモ

メモをPC内へ保存し、Foundry Local で音声の文字起こし、要約、タグ・分類の提案を行います。

## 構成

| ファイル | 役割 |
|---|---|
| `MainWindow.xaml` | 画面の部品の配置 |
| `MainWindow.xaml.cs` | ボタンの処理（入力→AI呼び出し→候補の表示）、録音、保存、絞り込み |
| `LocalAiService.cs` | Foundry Local の係（初期化・モデル準備・文字起こし・文章整理・解放） |
| `AiPrompts.cs` / `Strings/ja-JP/Prompts.resw` | AIへのお願い（プロンプト） |
| `MemoStore.cs` / `MemoItem.cs` | メモのJSON保存と1件分のデータ |
| `Categories.cs` | 分類のキーと表示名 |
| `Strings/ja-JP/Resources.resw` | 画面の文言（`x:Uid` で参照） |

## 動作環境

- Windows 11 24H2（ビルド 26100）以降、x64 または ARM64
- .NET 10 SDK、Visual Studio（WinUI アプリケーション開発）または winapp CLI
- 初回のモデル取得にインターネット接続と、数GBの空き容量
- 音声入力を使う場合はマイク

パッケージ: `Microsoft.AI.Foundry.Local` 1.2.4

## 起動

Visual Studio では「LocalAiMemoApp (Package)」で実行します。ターミナルからは次のとおりです。

```powershell
dotnet run -p:Platform=x64
```

ARM64 の PC では `x64` を `ARM64` に置き換えます。

## 使い方

1. 「AIを準備する」を押します。初回は文章用の `phi-4-mini` と音声用の `whisper-medium` を取得します。
2. 本文を手入力するか、「録音開始」→「停止して文字にする」で話した内容を本文の最後に追加します。
3. 「要約する」「タグと分類を提案する」で候補を作り、確認・修正してから「保存する」を押します。
4. 左側の「分類で絞り込む」「タグで絞り込む」で一覧を絞り込めます。

メモはパッケージのローカル保存領域に `memos.json` として保存します。自動保存はしません。
AIの返事は候補です。日付・数字・名前は元の文章と比べてください。
