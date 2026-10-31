# Windows版の開発と検証

要求の正本は[requirements.md](requirements.md)、回答記録は[interview-log.md](interview-log.md)です。対象範囲と完成条件は変更しません。

## 今回の実装単位

1. WindowsのConPTYで端末プロセスの起動、UTF-8入出力、リサイズ、終了を検証する独立したプロトタイプを作ります。GUI技術や製品全体の実装言語の採用は、この検証だけでは確定しません。
2. 本家mainの比較コミットを保存し、追加コミットと変更ファイルを記録するツールを作ります。機能への影響は人が確認し、対照表と検証へ反映します。
3. 調査・実装・検証の結果と未完了事項をこの文書から参照できるようにします。

## 調査開始時点

- fork: `cardamon04/fork-cmux`、作業開始コミット `7ac7646ed`。
- 本家: `https://github.com/manaflow-ai/cmux.git` の `main`。2026-10-31に取得した参照先は `2e4c17c63767048f6a790b071f87e7eca68ff8ae`。
- 本体はmacOS向けSwift/AppKit実装です。Windowsデスクトップ版はこのforkにまだありません。
- 既存の候補として[本家PR #5049](https://github.com/manaflow-ai/cmux/pull/5049)にElectron/ConPTYのWindows版、`cmux-tui/`にportable-ptyを使う実装があります。採用・移植は未決定です。既存候補のコードは今回コピーしません。
- forkのIssuesは無効です。ユーザーの指定に従い、進捗・仕様確認・判断依頼はこのforkのDraft PRに集約します。

## 次の検証

端末の技術検証後に、既存Windows版候補の機能差・本家への追従コスト、ブラウザ自動操作、公式クラウド・iPhoneの認証と接続方式を調べます。クラウド・iPhone連携は要件に残します。現時点で接続できないと判明したわけではありません。

製品の完成には全機能の対照表、PowerShell・WSL2・SSHでの実動作、Codex・Claude Code、IME、導入・更新、標準規模で1営業日の受入検証が必要です。端末プロトタイプの成功だけで完成とは判定しません。
