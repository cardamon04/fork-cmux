# Windows端末の技術検証

WindowsのConPTYを直接使い、端末プロセスの起動、UTF-8入出力、リサイズ、終了を確認します。製品のGUI、VT描画器、CLIサーバーは含みません。.NETを製品全体に採用する決定ではありません。

Windows build 17763以降と.NET 10 SDKが必要です。追加のNuGet依存はなく、`NuGet.Config`は外部パッケージソースを使いません。リポジトリのルートで実行します。

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$pwshPath = (Get-Command pwsh).Source
dotnet run --project Prototypes/WindowsTerminal -- --self-test $pwshPath
```

実行結果はJSON、各試験の進捗は標準エラーへ出力します。失敗は終了コード1、未対応環境や対象試験がない場合は2です。PowerShellのパスを省略した場合、その試験は未実行として出力します。特定の試験だけを再実行する場合は、パスの後ろにJSON中の試験名を指定します。

```powershell
dotnet run --project Prototypes/WindowsTerminal -- --self-test $pwshPath resize-reaches-client
```

## 確認する動作

- 日本語・絵文字を実際のコンソール入力として子プロセスへ渡し、子が読んだ文字列と終了コードを確認します。
- 100×30から132×43への変更を、子のConsole APIから確認します。
- 空白・日本語を含む作業ディレクトリと、空文字・引用符・末尾のバックスラッシュを含む引数を確認します。
- 大量出力と終了、出力の受け手が例外を投げた場合の排出継続、起動失敗時の解放を確認します。
- 10端末を同時に起動し、それぞれ固有の応答と終了を確認します。4エージェント・10ペインの製品受入検証を代替するものではありません。
- PowerShellはプロファイルを読み込まず、セッション内の入出力エンコーディングをUTF-8に指定したread/evalループで、日本語入力・コマンド実行・終了を確認します。ユーザーのプロファイルやシステムのコードページは変更しません。PSReadLine、IME、GUI上のキー操作は未検証です。

CIでは`Fixtures/FakeShell`も使い、入力をエコーするだけでコマンドを実行しないクライアントがPowerShell試験に誤合格しないことを確認します。この負の試験は期待どおり終了コード1になる場合に成功です。

## 所有権と制限

`ConPtySession`の入力・リサイズ・終了は単一の呼び出し元が所有します。出力は専用スレッドで読み続けます。出力のコールバックはブロックせずに戻る必要があります。入力の書き込みは同期処理であり、製品GUIへそのまま組み込むための非同期APIではありません。

終了時も出力を読み続け、ConPTYが送る最終フレームによるデッドロックを避けます。Windows 11 24H2以降は終了APIが即座に戻るため、所有する子プロセスの終了も上限10秒で待ちます。テスト用の出力蓄積は2 Mi文字を上限とし、超過時は成功扱いせず失敗します。ConPTYの終了API自体が待ち続ける場合に備え、CIジョブにも5分の上限を設けています。

親の標準入出力がパイプの場合も、子にはConPTYを接続します。`STARTF_USESTDHANDLES`と空の標準ハンドルを指定し、親のパイプが意図せず子へ複製されるケースを避けます。実行中のユーザーの端末には接続せず、試験で作った子プロセスとConPTYだけを閉じます。

参照: [ConPTYセッションの作成](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)、[終了APIの仕様](https://learn.microsoft.com/en-us/windows/console/closepseudoconsole)、[Microsoft Terminalの標準ハンドルに関する説明](https://github.com/microsoft/terminal/discussions/15814)。
