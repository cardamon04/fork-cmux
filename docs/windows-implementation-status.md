# Windows実装の到達点と残作業

2026-11-01時点の[要件](requirements.md)とDraft PR #1〜#18に対する整理です。各PRは未マージで、後続PRは直前のPRに依存します。mainで使える完成製品とは扱いません。[機能対照表](windows-feature-inventory.md)は調査中であり、本表も全機能の網羅・合格を示しません。最新コミットの実行結果とレビュー証拠は対応PRを確認します。

| 要件 | 実装した独立部分 | 残る実装・検証 |
| --- | --- | --- |
| F-01 | 作成・選択・終了、分割・リサイズ、名前・順序、live pane移動（#3/#9/#11/#12）。大小文字だけの名前変更もnative行と一致（#18） | drag/drop、pin/group、複数window、本家操作の全比較 |
| F-02 | ConPTY、両PowerShell、検索、Unicode入力、CWD、CDP composition、font zoom（#1/#3/#6/#7/#8/#13/#16） | 実OS IME、実PC GUI、全描画/protocol互換、WSL/SSH。pwsh過去prompt表示差は未解決 |
| F-03 | local CWD、Git branch/切り離されたHEAD（#7/#10） | PR・port・最新通知、remote作業先 |
| F-04 | 端末BELのin-app未読・強調・移動（#17） | agent hook/CLI受信、一覧、Windows toast、音、phone転送。BEL部分だけで全通知合格にしない |
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

## 次の独立単位を選ぶ理由

要件は本家の全機能対応と普段の作業再現を優先します。上記不具合について既存証拠で安全に切り分けた後、未実装F-04のうち認証・永続保存・実PC前面操作に依存しない**端末BELの未読表示と移動**を選びます。本家`DockSplitStore+AttentionRouting`のvisual BEL経路を比較対象にします。

未読はlive terminal paneの一時状態です。inactive pane/workspace/windowのBELで強調し、実際にその端末へfocusを戻したときに既読にします。受信だけでfocusを奪わず、移動後は新しいownerへ通知します。OSCのBEL終端は通知せず、BEL floodはrendererごとに100msのleading cooldownで抑えます。保存schemaやOS通知設定は増やしません。

本家`manaflow-ai/cmux:main`の日次差分確認を継続します。実装開始時の比較先は`4ac6e872ac1982c6a82bd633d23ce4d0e348ff1f`で、#16終了時と同じです。更新は互換修正が必要な差分だけを個別評価し、無関係な一括取り込みは行いません。
