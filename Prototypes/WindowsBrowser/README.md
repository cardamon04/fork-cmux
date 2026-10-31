# Windows内蔵ブラウザの技術検証

WebView2の実エンジンを非表示のHWNDでホストし、Chromium DevTools Protocol（CDP）経由の基本操作を検証します。GUI方式を採用する決定でも、本家のブラウザドライバの完成版でもありません。[候補比較と設計上の境界](../../docs/windows-architecture-comparison.md)を参照してください。

Windows、.NET10 SDK、Microsoft Edge WebView2 Runtimeが必要です。SDKはMicrosoft公式NuGetの`Microsoft.Web.WebView2`バージョン`1.0.4314.35`に固定し、`packages.lock.json`で取得内容を固定します。Runtimeのバージョンは実行結果へ記録します。この実行物はRuntimeをインストールしません。

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore Prototypes/WindowsBrowser --locked-mode
dotnet build Prototypes/WindowsBrowser --no-restore --nologo -warnaserror
dotnet run --project Prototypes/WindowsBrowser --no-build -- --self-test
```

絞り込みは`--self-test unicode-input-and-trusted-click`など、JSONに出る試験名を指定します。成功は終了コード0、失敗は1、該当試験がない場合は2です。試験は次の6件です。

- ナビゲーション、履歴、再読み込み、JS評価と日本語の結果。
- 日本語・絵文字の入力と、ページ側で`isTrusted`を確認するCDPマウスクリック。
- ボタンとテキスト入力のアクセシビリティツリー。
- consoleイベント、networkイベント、JSON応答。
- 異なる一時プロファイルのcookie/localStorage分離と、終了後の削除。
- 外部ナビゲーション、事前キャンセル、JS例外、終了済みブラウザの操作を成功扱いしないこと。

ページは`https://cmux-probe.invalid`へのリクエストをプロセス内で処理する固定fixtureです。実際のユーザープロファイルやサイトの認証情報へ接続しません。ブラウザのデバッグ用TCPポートやアプリの公開APIは作りません。

作成したプロファイルだけを使用し、コントローラーを閉じた後に`BrowserProcessExited`を待ってから削除します。外側のHWNDが非表示のままであることも各試験で確認します。ユーザーのブラウザを終了せず、リポジトリやOSのセキュリティ設定、既定ブラウザを変更しません。

目視したGUI、画面キャプチャ、IME、複数フレーム、要素参照、ページの権限、実アカウント連携、本家の全REPL/CLI/APIはこの試験に含みません。制限したfixtureでのCDPの可否を示す試験であり、製品の機能合格とは区別します。
