# Windows版の機能対照表（調査開始版）

比較元は`manaflow-ai/cmux`の`main`、コミット`2e4c17c63767048f6a790b071f87e7eca68ff8ae`です。参照先のコード・文書から確認対象を分けた初期一覧であり、全機能の調査完了を示すものではありません。CLI・API・設定・テストからさらに項目を追加する必要があります。

Windows製品は実装途中です。[デスクトップ](../windows/README.md)でワークスペース・分割・PowerShell端末の実操作を追加しましたが、下表の機能群全体の合格ではありません。プロトタイプや部分的な試験の成功を「合格」「同等機能で合格」へ読み替えません。各項目の対応方法、具体的な試験、Windows側のコミット、実行結果と証拠、残る差分を記録します。

| ID | 要件 | 確認する動作 | 本家の参照先 | Windows側の方法・検証状況 |
| --- | --- | --- | --- | --- |
| WIN-001 | F-01 | ウィンドウとワークスペースの作成・移動・終了 | [CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | デスクトップで作成・選択・終了と端末の独立性、手動の名前変更を実装。sidebarダブルクリックとtoolbarを共通処理へ接続し、取消・入力境界・重複名・pane分離・既存Title欄での保存/通常終了/復元・全9言語を確認。並べ替え・複数ウィンドウ・自動titleなど全体は未完了 |
| WIN-002 | F-01 | 縦・横タブ、分割、移動、サイズ変更、フォーカス | [dock](dock.md)、[CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | 縦横分割・ドラッグサイズ変更・選択・終了時の畳み込みを実装。タブ・移動など未実装 |
| WIN-003 | F-02 | PowerShellの起動・入出力・サイズ変更・終了 | [GhosttyTerminalView](../Sources/GhosttyTerminalView.swift)、[CmuxTerminal](../Packages/macOS/CmuxTerminal/) | ConPTYとxterm.jsをアプリへ統合。対話PowerShell/PSReadLine・Unicode・SGR色・サイズ伝播・終了を実操作で検証。IME処理キーの検索分離とcomposition候補・確定・取消をChromium/CDPと実ConPTY・両PowerShellで検証。OS実IME・全端末互換は未検証 |
| WIN-004 | F-02 | WSL2上の端末アプリの操作 | [CmuxTerminal](../Packages/macOS/CmuxTerminal/) | WSL固有の起動・作業先・パス変換を検証する必要あり |
| WIN-005 | F-02 | スクロール、検索、選択、コピー・貼り付け、マウス、画像 | [CmuxTerminal](../Packages/macOS/CmuxTerminal/)、[CmuxTerminalCore](../Packages/macOS/CmuxTerminalCore/) | スクロール・選択・コピー/貼り付けに加え、ペイン内の文字列検索・前後移動・大文字小文字切替を実装。実PowerShell出力・スクロールバック・Unicode・折り返し・該当なし・選択コピー・ワークスペース分離・終了・上限を検証。正規表現・全ペイン検索・検索キー設定・OSクリップボード・画像・Ghosttyとの差は未検証/未実装 |
| WIN-006 | F-03 | CWD、Git、PR、待受ポート、最新通知の表示 | [README](../README.md)、[CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | 部分実装。PowerShell 7/Windows PowerShell 5.1 のローカルCWD追跡・選択ペインの表示・分割先への継承を実装。既存プロンプト・終了状態・ペイン分離・不正/リモート/junction報告の拒否を実動作で検証。Git・PR・ポート・通知、WSL/SSHは未対応 |
| WIN-007 | F-04 | 通知の受信、未読、ペイン強調、一覧、移動 | [TerminalNotificationStore](../Sources/TerminalNotificationStore.swift)、[CmuxNotifications](../Packages/macOS/CmuxNotifications/) | 未実装 |
| WIN-008 | F-04 | Windowsのデスクトップ通知 | [CmuxNotifications](../Packages/macOS/CmuxNotifications/) | OS向け実装と通知からの復帰を要検証 |
| WIN-009 | F-05 | CLIコマンド・入出力・エラーの互換 | [cli-contract](cli-contract.md)、[command tree](cli-command-tree.txt)、[CLI](../CLI/) | コマンド単位の対照表へ展開する必要あり |
| WIN-010 | F-05 | API、識別子、認可、PowerShell・WSL2・SSHからの操作 | [CmuxControlSocket](../Packages/macOS/CmuxControlSocket/) | 通信方式・認可境界を設計する必要あり |
| WIN-011 | F-06 | 内蔵ブラウザ、ナビゲーション、プロファイル、データ取り込み | [BrowserPanel](../Sources/Panels/BrowserPanel.swift)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | デスクトップへブラウザペイン・URL入力・履歴・再読み込み・開発者ツールを統合。端末とは別の一時プロファイルで混在分割・切り替え・終了を検証。永続化・取り込み・ダウンロード・新規ウィンドウ・権限UIなどは未実装 |
| WIN-012 | F-07 | スナップショット、要素参照、クリック、入力、JS評価 | [browser protocol](browser-repl/driver-protocol.md)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | 実WebView2のAXツリー・CDP入力/クリック・JSを技術検証。要素参照・フレーム/所有権/権限など製品契約は未実装 |
| WIN-013 | F-07 | コンソール・ネットワーク確認と開発者向け機能 | [browser-repl](browser-repl/README.md)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | 実WebView2のCDPイベントを技術検証。製品のREPL/CLI/APIは未実装 |
| WIN-014 | F-08 | SSHワークスペース、初期コマンド、再接続 | [CmuxRemoteWorkspace](../Packages/macOS/CmuxRemoteWorkspace/)、[CmuxRemoteSession](../Packages/macOS/CmuxRemoteSession/) | ssh.exeの起動だけで合格としない |
| WIN-015 | F-08 | localhost転送、リモートブラウザ、ファイル・画像転送 | [CmuxRemoteDaemon](../Packages/macOS/CmuxRemoteDaemon/)、[CmuxSSHClipboardTrust](../Packages/macOS/CmuxSSHClipboardTrust/) | 未実装 |
| WIN-016 | F-09 | Codexの対話、フック、通知、状態、再開、分割連携 | [agent-hooks](agent-hooks.md)、[agent-recovery](agent-recovery.md)、[CMUXAgentLaunch](../Packages/macOS/CMUXAgentLaunch/) | 実エージェントを用いた検証は未実行 |
| WIN-017 | F-09 | Claude Codeの対話、フック、通知、状態、再開、チーム連携 | [agent-hooks](agent-hooks.md)、[CMUXAgentLaunch](../Packages/macOS/CMUXAgentLaunch/) | 実エージェントを用いた検証は未実行 |
| WIN-018 | F-09 | その他の本家エージェント・チャット連携 | [CmuxAgentChat](../Packages/Shared/CmuxAgentChat/)、[agent-messages](agent-messages.md) | 提供元と対応機能を列挙する必要あり |
| WIN-019 | F-10 | レイアウト・CWD・履歴・ブラウザ状態の保存と復元 | [SessionPersistence](../Sources/SessionPersistence.swift)、[agent-recovery](agent-recovery.md) | 部分実装。[Windowsアプリ](../windows/README.md)で終了時保存・次回起動時復元、混在分割・比率・選択・PowerShellのローカルcd後のディレクトリ・ブラウザURLに対応。保存項目は既存Directoryのままでコマンド/出力/履歴を追加保存しない。履歴・スクロールバック、ブラウザ履歴・ログイン状態、クラッシュ復旧等は未対応。F-10全体の合格ではない |
| WIN-020 | F-10 | 実行中プロセスの維持、再接続、エージェント再開 | [CmuxAgentSessionStore](../Packages/macOS/CmuxAgentSessionStore/)、[agent-recovery](agent-recovery.md) | 未実装 |
| WIN-021 | F-11 | Ghostty設定、フォント、テーマ、色、設定再読み込み | [configuration](configuration.md)、[CmuxSettings](../Packages/macOS/CmuxSettings/) | 設定キーごとの互換表が必要 |
| WIN-022 | F-11 | Windows向けキー割り当て、変更、カスタムコマンド、補完 | [ghostty-keybindings](ghostty-keybindings.md)、[configuration](configuration.md) | Ctrl+Cなどの通常操作とIMEを含めて検証する必要あり |
| WIN-023 | F-12 | 本家クラウドの認証、マシン、端末、ブラウザ | [CmuxCloud](../Packages/macOS/CmuxCloud/)、[CmuxCloudMachines](../Packages/macOS/CmuxCloudMachines/)、[CMUXAuthCore](../Packages/Shared/CMUXAuthCore/) | Windowsからの認証・接続は未検証。対象に維持 |
| WIN-024 | F-13 | 公式iPhoneのペアリング、端末操作、通知、再接続 | [CmuxIrohTransport](../Packages/Shared/CmuxIrohTransport/)、[iroh-offline-pairing](iroh-offline-pairing-v1.md)、[iOS](../ios/) | 実機・アカウントでの接続は未検証。対象に維持 |
| WIN-025 | F-14 | 個人用wingetマニフェスト、導入・起動・初回設定 | [requirements](requirements.md) | 導入物がないため未実装。システムの利用許可設定は変更しない |
| WIN-026 | F-15 | アプリ内更新確認・自動更新、状態引き継ぎ、失敗確認 | [CmuxUpdater](../Packages/macOS/CmuxUpdater/) | Windows向け配信方式・署名・保存状態との境界を要検証 |
| WIN-027 | F-16 | コマンドパレット、ファイル操作、Markdown表示など | [README](../README.md)、[CLI](../CLI/) | 利用者向け操作をさらに分解する必要あり |
| WIN-028 | F-16 | Feed、automations、computer useなどの追加機能 | [feed](feed.md)、[automations](automations.md)、[computer-use](computer-use.md) | 対象を落とさず、実装と利用条件を調査する必要あり |

最新の追加機能と未調査領域は[差分追跡手順](windows-development.md)に従って追加します。全行を合格にするだけでなく、一覧自体の網羅性を本家のCLI・API・設定・文書・テストに照らして確認することが完成条件です。
