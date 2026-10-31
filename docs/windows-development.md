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

## 実装と検証の入口

- [操作できるWindowsデスクトップの起動・検証](../windows/README.md)。ワークスペース・縦横分割・対話PowerShellと端末内検索、ローカル作業ディレクトリの追跡・表示・分割への継承、隣に開くブラウザ、作業配置の保存・次回起動時の復元を実装しています。保存復元は確認済みローカルディレクトリ・配置・選択・ブラウザURLまでの部分対応であり、全機能・受入条件の合格ではありません。
- [ConPTYプロトタイプの実行方法](../Prototypes/WindowsTerminal/README.md)
- [既存候補の比較とブラウザ設計](windows-architecture-comparison.md)、[WebView2検証の実行方法](../Prototypes/WindowsBrowser/README.md)。この検証でもGUI方式の採用は確定しません。
- [機能対照表の初期一覧](windows-feature-inventory.md)。28項目へ分解しましたが、網羅性の確認はまだ必要です。
- [進捗・仕様確認用Draft PR #1](https://github.com/cardamon04/fork-cmux/pull/1)

2026-10-31のローカル検証環境はWindows build 26300、x64（AMD64 Family 25 Model 33）、.NET SDK 10.0.400 / runtime 10.0.11、PowerShell 7.6.5、Python 3.12です。ConPTYの9試験が成功しました。日本語・絵文字の入出力、リサイズ、引数・CWD、大量出力、受け手の失敗、プロセス終了、起動失敗、10端末同時実行、PowerShellを対象にしています。描画・IME・WSL2・SSH・実エージェント・公式クラウド・iPhone・導入更新・1営業日の受入検証は未実施です。

## 本家mainの継続的な確認

開始時の比較コミットは[windows-upstream-baseline.json](windows-upstream-baseline.json)に保存しています。次の操作は取得と差分の記録までであり、マージや製品の合格判定は行いません。

```powershell
git fetch upstream main
python scripts/windows/upstream_report.py --format markdown
python tests/test_windows_upstream_report.py -v
```

`upstream`が未登録の場合は、最初に`git remote add upstream https://github.com/manaflow-ai/cmux.git`を実行します。レポートはローカル参照を読むため、取得に失敗した場合に「最新確認済み」として扱わないでください。検証開始時・完成判定前の確認に加え、継続開発では1日1回程度の確認を推奨します。

レポートには比較元・比較先・Windows側のコミット、未コミット変更の有無、追加コミット、変更ファイル、機能群の確認候補を出力します。候補の自動分類は網羅性を保証しません。変更が未知の領域でもF-16として必ず確認に残し、純粋な差分がゼロでも追加コミットがあれば確認対象にします。

変更を確認したら、利用者向け動作への影響、Windows固有処理との衝突、必要な試験をPRへ記録し、対照表を更新します。その後に基準JSONのコミットを明示的に進めます。ツール自身は基準を変更しません。履歴がつながらない場合は失敗にし、勝手に新しい基準へ置き換えません。

今回の取得では`908f3a9158684139f7369302cbe965ebb2d660b2`を検出しました。`scripts/ci/owned_pool_rescue.py`が監視対象に本家の`cmux-next-guards.yml`を加える変更で、Windows製品への機能追加はありません。今回のWindowsファイルとの直接衝突はありません。元の比較基準を残しているため、次回も未取り込みとしてレポートされます。

新しいWindows実装を既存のSwift/AppKitファイルへ広く混在させると、追従時の衝突が増えます。端末・ブラウザ・通知・更新・通信などのOS境界を分け、CLI/API契約と共有可能なデータ・処理を実装候補ごとに比較します。現時点で、既存Windows派生版をそのまま採用する決定はしていません。

## 自動検証

[Windows foundation](../.github/workflows/windows-foundation.yml)は、この実装に関係するPR更新時にPythonの追跡ツール試験とWindowsのConPTY試験を実行する定義です。GitHub側の実行結果はPRで確認します。定義があることと実行成功は別です。既存のmacOS本体のビルド・GUI試験はこの変更のローカル検証には含めません。

[Windows browser engine](../.github/workflows/windows-browser.yml)は、実WebView2でナビゲーション・JS・日本語入力・trusted click・アクセシビリティ・console/network・プロファイル分離を検証します。表示する製品GUIや本家ドライバ全体の合格判定とは区別します。日本時間の朝を基準に本家mainを日次確認する継続設定が有効です。進捗・仕様確認・判断依頼はGitHubに記録します。
