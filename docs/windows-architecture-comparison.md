# Windows実装候補の比較とブラウザ検証

2026-10-31時点の公開ソースを固定コミットで確認しました。候補のREADMEは実機検証の証拠として扱わず、この表ではソース・構成・候補側の記載を区別します。どちらの候補も、採用しただけでこのforkの完成条件を満たすものではありません。

| 候補 | 確認したコミット | GUI・端末・ブラウザ | 要件との主な差 | 追従時に確認する境界 |
| --- | --- | --- | --- | --- |
| [sweetcornna/cmux-for-windows](https://github.com/sweetcornna/cmux-for-windows/tree/c7e5960cfe23534833bc69b5fe0ea129b8c2768e) | `c7e5960cfe23534833bc69b5fe0ea129b8c2768e` | `windows/CmuxGui`はWinUI3/C#、Rustの`cmux-ffi`、Ghosttyの端末状態、DirectWrite/Direct2D描画、WebView2を使用。`CmuxGui.csproj`で確認 | `UPSTREAM.md`はAppleアプリ・iOS・web/cloud・Unix remote daemon/relayなどの削除を記録。READMEはSSH・クラウド・iOS、プロセス/履歴の復元、Windowsシステム通知などの未対応を明示 | Windows固有のGUI/描画は参考になるが、本家Rustと通信・クラウド・モバイルの構成を削除せず接続する検証が必要。候補の枝全体を置き換えとして取り込まない |
| [imthegoodboy/cmux-window / 本家PR #5049](https://github.com/manaflow-ai/cmux/pull/5049) | `69a959f123e975a9acb24bdcc17647d2ca1f2a2e` | `windows/package.json`はElectron42.3.0、xterm6.0.0、node-pty1.1.0、React/Vite。`src/main.cjs`はElectron BrowserWindowとwebview | 候補READMEはGhostty描画の同等性、自動更新、ショートカット/設定互換、WebView2ネイティブ自動操作の未完了を記載。本家サービス・公式iPhoneとの接続はこの調査では未検証 | 独立したWindows UIとnamed-pipe CLIがあり、本家CLI/APIの契約との照合が必要。大きなrenderer実装と本家側のドメイン変更の対応関係を追う必要あり |
| このforkの検証基盤 | [WindowsTerminal](../Prototypes/WindowsTerminal/)、[WindowsBrowser](../Prototypes/WindowsBrowser/) | ConPTYとWebView2のOS境界を独立した小さな実行物で検証 | 製品GUI・VT描画器・本家の全ブラウザドライバは未実装 | GUI方式の採用に先立ち、端末/ブラウザの契約と検証方法を固定する。プロトタイプの言語を製品全体の採用決定に読み替えない |

候補ソースは比較のために読み取りました。この実装単位ではコードをコピーしていません。上流のREADMEと各領域のコードを保持し、クラウド・iPhone・SSHを初回対象から外しません。

## 今回決められること

本家の[ブラウザドライバ契約](browser-repl/driver-protocol.md)は、エンジンに依存しない呼び出しとイベントを定義し、Chromium向けCDP接続も想定しています。WindowsのWebView2は[公式にCDPの呼び出しとイベントを提供](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/chromium-devtools-protocol)します。そのため、まずWindows側の実ブラウザで必要な基本操作を検証します。GUI全体がWinUI3、WPF、Electronのどれになるかは、この検証だけでは決めません。

`WindowsBrowser`は非表示のHWNDとSTAメッセージループでWebView2をホストします。OS上のウィンドウを表示・アクティブ化せず、ブラウザコントローラー内部ではレイアウトとCDP入力を有効にします。ページの日本語入力、trusted mouse click、アクセシビリティツリー、JS結果、console/networkイベント、履歴、再読み込み、プロファイル分離を実ブラウザで確認します。

これは本家の製品ドライバではありません。要素参照の寿命、iframeとcross-origin、セッション所有権、ドメイン制限、認証、タブ・プロファイル管理、キャンセル後の動作、ダウンロード、実際のGUI/IME/描画を含む全契約は別途実装・検証が必要です。今回のCDP成功だけをF-06/F-07の合格にしません。

## 継続開発と次の判断

日本時間の朝を基準に、本家`manaflow-ai/cmux`の`main`を日次確認する継続設定が有効です。進捗・仕様確認・判断依頼はGitHubで行い、メインチャットへの通知は行いません。

この検証開始時の最新取得は`d519a585c7fd9d8f3389f451bfc43c7416dfab93`です。前回取得からの変更はCIランナー選択とその試験で、Windowsブラウザの利用者向け動作への直接変更はありません。比較基準JSONは自動で進めません。

次は本家側で共有できる端末・レイアウト・通信のAPIを照合し、GUIと描画器の候補ごとにIME、10ペイン、復元、本家更新の取り込みを実機で比較します。ユーザーの判断が必要になる場合は、根拠と選択肢をGitHubに記録します。現在はIssuesが無効のため、既存のDraft PRを連絡先とし、設定変更は行いません。
