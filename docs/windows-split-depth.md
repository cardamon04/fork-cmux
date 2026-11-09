# Windowsの分割と保存形式の深度境界

F-01/F-10の既存実装では、workspaceへのペイン移動は保存形式の深度を確認しますが、通常の縦・横分割とbrowser追加は確認していません。深さ16のペインをもう一度分割すると、live layoutを作れてもversion-1の保存検証に通らなくなります。今回は新機能よりこの経路の修正を優先します。baseは未マージの#23 `d8104f4968b614fa479c30581b029fcb39d74aff`、成果と最終検証は[Draft PR #24](https://github.com/cardamon04/fork-cmux/pull/24)に記録します。#20は含みません。

## 共有する判定

選択されたペインがliveなworkspaceに属することと、親のsplitを数えた深さを一つの経路で確認します。native toolbarの分割・browser追加、直接呼ばれた分割処理、既存workspaceへの移動先判定が同じ境界を使います。深さ15はもう一度分割でき、深さ16はlive resourceを作る前に拒否します。深い枝があっても、浅いペインの分割まで止めません。閉じてsplitが畳まれた後は、再び分割できます。

`SessionSnapshot.MaxLayoutDepth=16`と保存versionを維持します。新しい保存項目やshell履歴は追加しません。workspace数64、全体ペイン数256、保存bytes上限など、別の容量境界の操作時チェックはこの修正の合格範囲に含めません。製品の最大数を要件上の10ペインへ制限する変更でもありません。

## 検証の範囲

`SplitDepthScenario`を既存のfull desktop suiteへ追加します。実WebView2のbrowserを含むdepth15の保存データをprivate storeから読み、実PowerShellを最後の有効な分割でdepth16にします。同じレイアウトに対する直接の縦分割・横分割・browser追加、native操作の無効化、浅いbrowserの分割と既存page/shell memoryの保持、closeによる深度解放、既存schemaのsave・通常終了・再開を両PowerShellで確認します。

test-only `9ce887f3a720944074c91839c13e56076c6146b6`の[隔離Windows CI](https://github.com/cardamon04/fork-cmux/actions/runs/42539742390)は、有効なdepth16・17ペイン・実端末起動後に、直接の縦分割でdepth17・18ペインとなり保存検証に失敗しました。setup/compile失敗とは区別します。元のdiscriminatorを変えず、修正HEADでも同じfull-suiteコマンドを実行します。以前の259件を消さずに実行し、実結果・未実行ケース・独立レビューと最終CIはPR #24に記録します。localはcompileのみです。

対象PCのGUI・physical入力・IME・OS clipboard、pwsh表示差、#14 paint差、履歴保存範囲・期間、4agent/10pane・1営業日の受入は未完です。過去の`DETACH_STATE` timeoutと#23のlate source-removal/nonzero-index rollback等の未検証は、今回の分割チェックが成功しても解決済みとしません。旧監視プロセスのOS状態も不明のままで、CIM再照会・代替列挙・監視プロセス追加を行いません。

## 本家mainの差分評価

開始前の取得は2026-11-09 00:14 UTC、mainは`7ce5aa1d9777c0309136eec93cd9836076729ac6`です。前回`40455a2f`から32 commits / 192 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み178 commitsです。差分追跡ツールで記録し、fetchを統合済みとは扱いません。

- 17件はRustのcmux-tui/daemon・API/spec・テストです。[keyboard copy/search](https://github.com/manaflow-ai/cmux/commit/3b56e9b307e7999ae3e3e011be33a56646f126c4)、[unwrapped read/exit status/output wait](https://github.com/manaflow-ai/cmux/commit/ea377bdaf5035ade5fe1bf205a2461dbf229b123)、[attention jump/CLI/host notification](https://github.com/manaflow-ai/cmux/commit/83305561d7d6f748d38dd007c94b8b60c0fc3410)、[agent status/done-until-seen](https://github.com/manaflow-ai/cmux/commit/196da3eb74c849e0018199de957f2aa47e02b7d1)、[identify/skill/SessionStart](https://github.com/manaflow-ai/cmux/commit/97a17e9c9693a941343e606e59ce89b7e3f687f9)、[own-build helper/plugin roster](https://github.com/manaflow-ai/cmux/commit/7ce5aa1d9777c0309136eec93cd9836076729ac6)はF-02/F-03/F-04/F-05/F-09の比較対象に残します。Windowsの検索/BELだけでこれらの契約を実装済みとしません。
- [起動直後のCWD report保持](https://github.com/manaflow-ai/cmux/commit/93f998a6aa1bab88483c6e166611454cf769d9b7)はRust resource rowのcommit前にreportを捨てない修正です。Windowsは`TerminalPane`に受理したCWDを保持し、開始前に購読を設定する別経路で、同じresource graphの公開待ちはありません。startup repair/journal/retirement等の変更も現行Windowsの保存形式とは別です。今後のdaemon/remote対応時の比較に残し、過去のDETACH_STATEの原因や修正とは結び付けません。
- 15件はCI・runner/cache・配布・検証基盤です。Windows runtimeの互換修正は見つからず、workflowやpool・時間上限・設定を取り込みません。既存CIのGit内部BUG、不足branch、Mac compile上限が解決したとは扱いません。

本家のopen PRを症状で検索しました。split競合の#26926やbounded session restoreの#24430/#25764は別の本家経路を対象とし、Windowsのこの修正に外部実装を流用していません。完成判定前にもmainを取得し、追加差分と本人回答・レビューを確認します。
