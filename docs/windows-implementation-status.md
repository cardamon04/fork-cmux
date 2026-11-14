# Windows実装の到達点と残作業

2026-11-14時点の[要件](requirements.md)とDraft PR #1〜#19・#21〜#26・#29に対する整理です。各PRは未マージで、#29は#26に依存し、BLOCK中の#20/#28と閉鎖済み#27を含みません。#28の新16件はログ上通過しましたが、全体DETACH_STATE失敗の帰属・345件保持・実checkout treeは未確定です。mainで使える完成製品とは扱いません。[機能対照表](windows-feature-inventory.md)は調査中であり、本表も全機能の網羅・合格を示しません。最新コミットの実行結果とレビュー証拠は対応PRを確認します。

| 要件 | 実装した独立部分 | 残る実装・検証 |
| --- | --- | --- |
| F-01 | 作成・選択・終了、分割・リサイズ、名前・順序、live pane移動（#3/#9/#11/#12）、既存workspaceへのlive split pane移動（#22）、最後のlive paneの移動と空sourceの破棄（#23）、通常分割の保存深度境界（#24）、workspace作成の既存保存件数・採番境界（#25）、追加操作の全体256ペイン境界（#26）。大小文字だけの名前変更もnative行と一致（#18） | 複数paneのworkspace一括merge、drag/drop、pin/group、複数window、本家操作の全比較 |
| F-02 | ConPTY、両PowerShell、検索、Unicode入力、CWD、CDP composition、font zoom（#1/#3/#6/#7/#8/#13/#16）。空selectionの検索入力でもCopyを端末出力から分離（#19） | 実OS IME、実PC GUI、全描画/protocol互換、WSL/SSH。pwsh過去prompt表示差は未解決 |
| F-03 | local CWD、Git branch/切り離されたHEAD（#7/#10） | PR・port・最新通知、remote作業先 |
| F-04 | 端末BELのin-app未読・強調・移動（#17）、未読端末の一覧と行選択（#21） | agent hook/CLI受信、通知本文・既読履歴、一覧のdigit shortcut、Windows toast、音、phone転送。BEL部分だけで全通知合格にしない |
| F-05 | 技術検証段階 | 製品CLI/APIの契約、識別子・認可・通信境界、コマンドごとの互換性 |
| F-06 | 混在browser pane、navigation、Stop、devtools、分離した一時profile（#2/#4/#15） | 取り込み、永続browserデータ、download/new-window/permission UI |
| F-07 | WebView2 AX/CDPの技術検証（#2） | 製品API、安定した要素参照・認可・失敗時契約 |
| F-08 | 未実装 | SSH/WSLの起動だけでなく、作業先・path/port・tunnel・transfer・再接続の確認 |
| F-09 | 未実装 | 実Codex/Claude Code、hook、session・再開の実動作 |
| F-10 | 既存schemaによるlayout/CWD/browser URLの保存・通常復元（#5/#7） | 履歴の範囲・期間は[既存質問](https://github.com/cardamon04/fork-cmux/pull/7#issuecomment-6549362861)の回答待ち。history、scrollback、login、crash/process再接続は未実装 |
| F-11 | 各端末の一時font zoom（#16） | 設定互換、font family/theme/color/reload、キー割当変更 |
| F-12/F-13 | 接続未検証 | 本家cloud・公式iPhoneの実account/pairing。新規認証・接続を既存許可と混同しない |
| F-14/F-15 | 未実装 | 個人用winget導入物、署名/配信/更新、状態移行・失敗復旧。設定・配布は別途許可が必要 |
| F-16 | 未実装・追加調査 | palette、file/Markdown、feed/automations/computer use等を個別項目に分解 |

## 既知不具合と未検証を分ける

- **pwsh表示差**：#16の実ConPTY出力/resize traceを、pinned xtermのinert replayで再現。最終bufferは元traceと一致し、単なるPNG差ではありません。cursor-line reflow/旧ConPTY modeへの切替でも解消せず、app-side原因は未確定です。予測表示の無効化や出力filterで隠しません。
- **#14のtitle paint差**：native captionの値とpaint/captureの差が残ります。実PC確認なしで解決扱いにしません。
- **未検証**：実PC GUI、physical入力/実OS IME、全面visual/history parity、4agent・10paneで1営業日の普段使い受入。hosted CDPや所有fixtureだけで代替合格にはしません。
- **履歴仕様**：元インタビューのQ1〜Q15は回答済みですが、実装時の保存対象・期間の追加質問は未回答です。command/outputを追加保存しません。
- **共通CI**：fast guards/Mac compileの上限とfork不足branch等は各最終HEADの実結果をPRに記録します。Windows lane成功とaggregate成功を混同せず、時間上限・設定・チェックを変更しません。

## 端末出力の全選択

#29はF-02の選択・コピーを日常利用できる単位として進めます。選択中の端末の保持出力を全選択し、その後の明示Copyで取得します。Find欄を閉じて検索文字列の所有権と混同せず、未送信入力と他pane/workspaceの選択を保ちます。browserでは無効です。保存仕様・認証・実PC操作やBLOCK修正に依存しません。[実行範囲と本家差分評価](windows-terminal-select-all.md)、[最終結果](https://github.com/cardamon04/fork-cmux/pull/29)を参照します。全selection/shortcut互換や実OS clipboard・IMEの合格とはしません。

## 前回の独立単位を選んだ理由

#26はF-01/F-10の保存整合性を守る修正です。全体256ペインの保存可能なnative graphから、workspace作成・通常分割で257ペインへ増えて保存検証に失敗する経路を両PowerShellで再現しました。通常workspace作成・terminal/browser分割が同じ追加上限を確認し、native actionも同じ判定を使います。既存ペインを新workspaceや既存workspaceへ移す操作は、ペイン数が増えないため維持します。closeで1個空けば再び追加でき、schema・履歴範囲・既存workspace数/採番/深度の制約は変えません。bytes上限は別課題です。少数の実エンジンと未起動モデルによるgraph検証で、256 live engineやGUI復元の受入とは区別します。[再現・検証範囲と本家42コミットの評価](windows-pane-capacity.md)を参照してください。

## 直前の最後のpane移動

#23はF-01の最後の1ペインを既存workspaceへ移す操作です。通常のworkspaceをまとめるために不要なsplitを作る制限を取り除きます。native adoption失敗では元workspaceを保ち、成功後だけ空containerを破棄します。#22のtransactionと通常closeのcontainer切り離しを共有し、新規workspaceへの移動条件・schema・history範囲は維持します。認証・履歴回答・前面操作や#20の再開に依存しません。[比較元の動作、本家6 commitsの影響と検証範囲](windows-last-pane-transfer.md)を記録します。

## 直前のpane移動

#22はF-01の既存workspaceへの移動を扱います。liveなterminal/browserを新規workspaceへ分けるだけでなく、既存の作業先へまとめ直す操作です。移動先の選択paneの右へ配置し、元のsplitをたたみます。認証・履歴仕様・実PC前面操作に依存せず、#20の修正再開も必要ありません。表示済みのsource paneとdestination workspaceを保持し、stale行は現在のowner・選択・生存を検証して拒否します。新規workspaceへの移動と同じtransactionでnative reparent失敗を戻し、既存の保存schemaを使います。当時未実装だった最後のpaneの移動は#23で追加しています。複数paneの一括mergeは未実装です。

開始時の本家mainは`be5e24339c6eabb8e929a7029c4e6f834e257c97`です。前回`407c048a`以降の31 commitsを[今回の差分評価](windows-pane-transfer.md)に記録します。CI・実動作・独立レビューの最終HEADはPR #22で確認します。

## 直前の独立単位

F-04で明記された通知一覧のうち、既存BELの未読端末を行から選ぶ操作を#21で扱います。4agent・10paneを使う際に「次の未読」以外から対象を選べる単位で、認証・履歴仕様・実PC前面操作に依存しません。#20のリンク修正は再開せず、#19から分岐した独立実装です。

未読は引き続きlive terminal paneの一時状態です。表示済み行はpaneを保持し、後着BELで対象を変えません。クリック時に生存状態・未読状態・現在のownerを確認し、既存の未読移動と同じ経路でfocus・既読化します。通知の永続保存、OS通知設定、新しいshortcutは追加しません。本家のmodifier/digitによる通知行操作は未実装として残します。

本家`manaflow-ai/cmux:main`の日次差分確認を継続します。#21開始時の比較先は`c9ed9bd7078e439efc1e09e50cd5577af33ee516`です。初期baselineと未完項目を保持し、[差分評価と検証範囲](windows-notifications-list.md)を記録します。
