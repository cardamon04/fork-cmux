# Windows本家差分の追跡記録

追跡先は`https://github.com/manaflow-ai/cmux.git`の`main`です。`git fetch --no-tags <repository> main`を明示実行した後、`python scripts/windows/upstream_report.py --target FETCH_HEAD --output <private-report>`で取得SHAと初期baselineからの差分を記録します。baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`は未完項目を見失わないため保持します。fetchはmergeではありません。

## PR #20: 端末リンクと内蔵ブラウザ

- 開始前の取得結果を2026-11-02 04:16 UTCのreport生成で照合: `4ac6e872ac1982c6a82bd633d23ce4d0e348ff1f`。
- 候補確定前取得: 2026-11-02 04:22 UTC、同じ`4ac6e872ac1982c6a82bd633d23ce4d0e348ff1f`。#19終了時から新しいupstream commitはありません。
- 修正候補確定前の再取得: 2026-11-02 04:43 UTC、同じSHA。初期baselineと累積42 commitsを保持。対象link経路の差分なし。
- URL完全性の2回目修正前にも2026-11-02 05:02 UTCに再fetchし同じSHAを確認。初期baselineからの累積差分は保持。
- 初期baselineから42 commits。今回比較した`Sources/TerminalLinkOpenCoordinator.swift`はbaselineとの内容差分がなく、リンクのsource containerを解決する内蔵browser経路への新しい互換修正は不要です。WindowsのHTTP(S)リンク操作をこの既存機能に対応させます。
- 累積差分の`AgentHookDeliveryProcess`/`TerminalController`、socket capability/authorization、tmux host runtimeは、未実装のagent/CLI/remote機能に関連します。#10以降の評価を引き継ぎ、リンク実装へ無関係なコードを取り込みません。全機能対応済みの判定ではありません。
- CI/fleetの更新は端末リンクの実行コードではありません。forkのGit内部BUG・不足branch・Mac compile上限を設定変更で回避せず、各最終HEADの結果をPRで別に記録します。

完成前にもfetchと最新回答・reviewを再確認し、同じSHAならPRの最終記録に観測時刻を追記します。別SHAなら差分を評価してから完了を判定します。日次追従は既存のスケジュールを使用します。
