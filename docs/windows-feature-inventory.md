# Windows版の機能対照表（調査開始版）

比較元は`manaflow-ai/cmux`の`main`、コミット`2e4c17c63767048f6a790b071f87e7eca68ff8ae`です。参照先のコード・文書から確認対象を分けた初期一覧であり、全機能の調査完了を示すものではありません。CLI・API・設定・テストからさらに項目を追加する必要があります。

Windows製品は実装途中です。[デスクトップ](../windows/README.md)でワークスペース・分割・PowerShell端末の実操作を追加しましたが、下表の機能群全体の合格ではありません。プロトタイプや部分的な試験の成功を「合格」「同等機能で合格」へ読み替えません。各項目の対応方法、具体的な試験、Windows側のコミット、実行結果と証拠、残る差分を記録します。

| ID | 要件 | 確認する動作 | 本家の参照先 | Windows側の方法・検証状況 |
| --- | --- | --- | --- | --- |
| WIN-001 | F-01 | ウィンドウとワークスペースの作成・移動・終了 | [CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | 作成・選択・終了、手動の名前変更に加え、選択workspaceの上下並べ替えを実装。既存の選択workspace/pane・実行中shell/browserを保ち、境界/無効操作・重複名・native行表示・既存配列/選択indexの保存/通常終了/復元を確認。renameと行更新の選択保護を共有。全9言語対応。drag/drop・pin/group・複数window・自動titleなど全体は未完了 |
| WIN-002 | F-01 | 縦・横タブ、分割、移動、サイズ変更、フォーカス | [dock](dock.md)、[CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | 縦横分割・ドラッグサイズ変更・選択・終了時の畳み込みに加え、選択paneを新workspaceへ移動する操作を実装。元の分割を畳み、同じshell/browser・CWD通知・移動先focusを保持。単一paneは移動不可。元workspaceの終了からの分離、native reparent失敗時の復旧、共有ClosePaneのcontrol/child handle解放、表示中toolbar overflowの選択target保護、await中に変わった同一pane内のfocus保護、既存schemaの保存/通常終了/復元を検証。browserはURL欄を選択。全9言語対応。選択端末のOSC 0/2 live titleをwindow captionへ表示する実装を追加。上限・Unicode・最新値集約と選択/保存との分離を背景で検証し、対象PCのGUI確認は待機。既存workspaceへのlive split pane移動（#22）と最後のpane移動・空source破棄（#23）を追加。タブ・複数paneのworkspace一括merge・他windowへの移動・drag/drop・workspace/tabの自動title・CLI/API互換などは未完了 |
| WIN-003 | F-02 | PowerShellの起動・入出力・サイズ変更・終了 | [GhosttyTerminalView](../Sources/GhosttyTerminalView.swift)、[CmuxTerminal](../Packages/macOS/CmuxTerminal/) | ConPTYとxterm.jsをアプリへ統合。対話PowerShell/PSReadLine・Unicode・SGR色・サイズ伝播・終了を実操作で検証。ConPTYの9001要求に応じた非ASCII textのWin32 record送信で、PS5.1のraw非BMP/結合文字欠落を修正。両PowerShellの実history/実CWD、cursor編集・Ctrl+C・複数行paste・交渉解除/分割requestを検証。IME処理キーの検索分離とcomposition候補・確定・取消はChromium/CDPと実ConPTYで検証。ConPTYの実OS buildをrendererへ伝え、行数拡大時のscrollback保持を既存window resizeとfont/pane操作の回帰で検証する。OS実IME・全Win32 keyboard/modifier・全端末互換は未検証 |
| WIN-004 | F-02 | WSL2上の端末アプリの操作 | [CmuxTerminal](../Packages/macOS/CmuxTerminal/) | WSL固有の起動・作業先・パス変換を検証する必要あり |
| WIN-005 | F-02 | スクロール、検索、選択、コピー・貼り付け、マウス、画像 | [CmuxTerminal](../Packages/macOS/CmuxTerminal/)、[CmuxTerminalCore](../Packages/macOS/CmuxTerminalCore/) | スクロール・選択・コピー/貼り付けに加え、ペイン内の文字列検索・前後移動・大文字小文字切替を実装。実PowerShell出力・スクロールバック・Unicode・折り返し・該当なし・選択コピー・ワークスペース分離・終了・上限を検証。検索入力のselectionが空でもCopyが端末出力へfallbackせず、端末へfocusを戻すと出力をCopyする境界を検証。検索navigationへfocusを移した後はqueryに残るselectionを使わず実際の一致をCopyする（#19）。正規表現・全ペイン検索・検索キー設定・OSクリップボード・画像・Ghosttyとの差は未検証/未実装 |
| WIN-006 | F-03 | CWD、Git、PR、待受ポート、最新通知の表示 | [README](../README.md)、[CmuxWorkspaces](../Packages/macOS/CmuxWorkspaces/) | 部分実装。両PowerShellでローカルCWD追跡・表示・分割継承と、選択ペインのGit branch/切り離されたHEAD表示を実装。branch変更・unborn/linked worktree・nativeのliteral描画・pane/workspace/browser分離・不正HEAD/管理外の消去・保存範囲/通常終了を検証。Git読取はUI外で実行し、所有local promisorの失敗回帰からlazy fetchを禁止。既存prompt/終了状態とCWDの不正/リモート/junction拒否の回帰も維持。dirty状態・PR・ポート・通知、WSL/SSHは未対応 |
| WIN-007 | F-04 | 通知の受信、未読、ペイン強調、一覧、移動 | [TerminalNotificationStore](../Sources/TerminalNotificationStore.swift)、[CmuxNotifications](../Packages/macOS/CmuxNotifications/) | 端末BELによるin-app未読・pane強調・次の未読paneへの移動を部分実装。実parserと両PowerShell/所有hosted GUIでframing・focus非干渉・移動/終了時owner・一時状態を検証する。OSCのBEL終端は除外、floodはrendererごとに抑制。実PCは未検証。hook/CLI受信・通知一覧・OS toast/音・phone転送は未実装 |
| WIN-008 | F-04 | Windowsのデスクトップ通知 | [CmuxNotifications](../Packages/macOS/CmuxNotifications/) | OS向け実装と通知からの復帰を要検証 |
| WIN-009 | F-05 | CLIコマンド・入出力・エラーの互換 | [cli-contract](cli-contract.md)、[command tree](cli-command-tree.txt)、[CLI](../CLI/) | コマンド単位の対照表へ展開する必要あり |
| WIN-010 | F-05 | API、識別子、認可、PowerShell・WSL2・SSHからの操作 | [CmuxControlSocket](../Packages/macOS/CmuxControlSocket/) | 通信方式・認可境界を設計する必要あり |
| WIN-011 | F-06 | 内蔵ブラウザ、ナビゲーション、プロファイル、データ取り込み | [BrowserPanel](../Sources/Panels/BrowserPanel.swift)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | デスクトップへブラウザペイン・URL入力・履歴・再読み込み・読み込み停止・開発者ツールを統合。端末とは別の一時プロファイルで混在分割・切り替え・終了を検証。Stop は pane ごとの pending navigation を停止し、状態・履歴/次の navigation・隣接 pane との分離を hosted CI で検証する。対象PCのGUIは待機。永続化・取り込み・ダウンロード・新規ウィンドウ・権限UIなどは未実装 |
| WIN-012 | F-07 | スナップショット、要素参照、クリック、入力、JS評価 | [browser protocol](browser-repl/driver-protocol.md)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | 実WebView2のAXツリー・CDP入力/クリック・JSを技術検証。要素参照・フレーム/所有権/権限など製品契約は未実装 |
| WIN-013 | F-07 | コンソール・ネットワーク確認と開発者向け機能 | [browser-repl](browser-repl/README.md)、[CmuxBrowser](../Packages/macOS/CmuxBrowser/) | 実WebView2のCDPイベントを技術検証。製品のREPL/CLI/APIは未実装 |
| WIN-014 | F-08 | SSHワークスペース、初期コマンド、再接続 | [CmuxRemoteWorkspace](../Packages/macOS/CmuxRemoteWorkspace/)、[CmuxRemoteSession](../Packages/macOS/CmuxRemoteSession/) | ssh.exeの起動だけで合格としない |
| WIN-015 | F-08 | localhost転送、リモートブラウザ、ファイル・画像転送 | [CmuxRemoteDaemon](../Packages/macOS/CmuxRemoteDaemon/)、[CmuxSSHClipboardTrust](../Packages/macOS/CmuxSSHClipboardTrust/) | 未実装 |
| WIN-016 | F-09 | Codexの対話、フック、通知、状態、再開、分割連携 | [agent-hooks](agent-hooks.md)、[agent-recovery](agent-recovery.md)、[CMUXAgentLaunch](../Packages/macOS/CMUXAgentLaunch/) | 実エージェントを用いた検証は未実行 |
| WIN-017 | F-09 | Claude Codeの対話、フック、通知、状態、再開、チーム連携 | [agent-hooks](agent-hooks.md)、[CMUXAgentLaunch](../Packages/macOS/CMUXAgentLaunch/) | 実エージェントを用いた検証は未実行 |
| WIN-018 | F-09 | その他の本家エージェント・チャット連携 | [CmuxAgentChat](../Packages/Shared/CmuxAgentChat/)、[agent-messages](agent-messages.md) | 提供元と対応機能を列挙する必要あり |
| WIN-019 | F-10 | レイアウト・CWD・履歴・ブラウザ状態の保存と復元 | [SessionPersistence](../Sources/SessionPersistence.swift)、[agent-recovery](agent-recovery.md) | 部分実装。[Windowsアプリ](../windows/README.md)で終了時保存・次回起動時復元、混在分割・比率・選択・PowerShellのローカルcd後のディレクトリ・ブラウザURLに対応。保存項目は既存Directoryのままでコマンド/出力/履歴を追加保存しない。履歴・スクロールバック、ブラウザ履歴・ログイン状態、クラッシュ復旧等は未対応。F-10全体の合格ではない |
| WIN-020 | F-10 | 実行中プロセスの維持、再接続、エージェント再開 | [CmuxAgentSessionStore](../Packages/macOS/CmuxAgentSessionStore/)、[agent-recovery](agent-recovery.md) | 未実装 |
| WIN-021 | F-11 | Ghostty設定、フォント、テーマ、色、設定再読み込み | [configuration](configuration.md)、[CmuxSettings](../Packages/macOS/CmuxSettings/) | 選択端末の文字サイズ増減・リセットを実装。pane ごとの一時 zoom（6〜72 CSS px、初期値14）と既存 ConPTY resize 経路を使用。両 PowerShell の実 grid・入力保持・pane/workspace/browser 分離・移動を hosted CI で検証する。対象 PC GUI は待機。永続設定・Ghostty 設定互換・フォント選択・テーマ/色・再読み込みは未実装 |
| WIN-022 | F-11 | Windows向けキー割り当て、変更、カスタムコマンド、補完 | [ghostty-keybindings](ghostty-keybindings.md)、[configuration](configuration.md) | Ctrl+Cなどの通常操作とIMEを含めて検証する必要あり |
| WIN-023 | F-12 | 本家クラウドの認証、マシン、端末、ブラウザ | [CmuxCloud](../Packages/macOS/CmuxCloud/)、[CmuxCloudMachines](../Packages/macOS/CmuxCloudMachines/)、[CMUXAuthCore](../Packages/Shared/CMUXAuthCore/) | Windowsからの認証・接続は未検証。対象に維持 |
| WIN-024 | F-13 | 公式iPhoneのペアリング、端末操作、通知、再接続 | [CmuxIrohTransport](../Packages/Shared/CmuxIrohTransport/)、[iroh-offline-pairing](iroh-offline-pairing-v1.md)、[iOS](../ios/) | 実機・アカウントでの接続は未検証。対象に維持 |
| WIN-025 | F-14 | 個人用wingetマニフェスト、導入・起動・初回設定 | [requirements](requirements.md) | 導入物がないため未実装。システムの利用許可設定は変更しない |
| WIN-026 | F-15 | アプリ内更新確認・自動更新、状態引き継ぎ、失敗確認 | [CmuxUpdater](../Packages/macOS/CmuxUpdater/) | Windows向け配信方式・署名・保存状態との境界を要検証 |
| WIN-027 | F-16 | コマンドパレット、ファイル操作、Markdown表示など | [README](../README.md)、[CLI](../CLI/) | 利用者向け操作をさらに分解する必要あり |
| WIN-028 | F-16 | Feed、automations、computer useなどの追加機能 | [feed](feed.md)、[automations](automations.md)、[computer-use](computer-use.md) | 対象を落とさず、実装と利用条件を調査する必要あり |

最新の追加機能と未調査領域は[差分追跡手順](windows-development.md)に従って追加します。全行を合格にするだけでなく、一覧自体の網羅性を本家のCLI・API・設定・文書・テストに照らして確認することが完成条件です。

Draft群の到達点、既知不具合、仕様回答待ちと未検証の区別は[実装状況](windows-implementation-status.md)にまとめます。
