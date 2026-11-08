# Windowsで最後のlive paneをまとめる

F-01の移動のうち、workspaceの最後のterminal/browserも既存workspaceへ移せるようにします。#22のsplit限定を広げる単位です。baseは未マージの#22 `aca2f2cd6d791696a1b376b115bb10ef8b6f7d91`、成果と最終検証は[Draft PR #23](https://github.com/cardamon04/fork-cmux/pull/23)に記録します。BLOCK中の#20を含まず、その修正上限や本人回答待ちを変更しません。

## 動作と所有権

通常の新しいworkspaceは1ペインで始まります。これを既存の作業先へまとめるために、一時的なsplitや新しいshellを作る必要がなくなります。既存の「workspaceへペインを移動」メニューを使い、移動先の選択ペインの右に同じlive controlを配置します。元のsplitに他のペインがある場合の動作は共有transactionのままです。

本家`40455a2f4326d030f52b158684a0c48242b568ca`の[`moveSurface`](https://github.com/manaflow-ai/cmux/blob/40455a2f4326d030f52b158684a0c48242b568ca/Sources/AppDelegate.swift#L6062)は成功後に[`cleanupEmptySourceWorkspaceAfterSurfaceMove`](https://github.com/manaflow-ai/cmux/blob/40455a2f4326d030f52b158684a0c48242b568ca/Sources/AppDelegate.swift#L6945)を呼び、空の元workspaceを閉じます。Windowsでも成功後だけ空コンテナを破棄します。shell/browser自体は移動先へ引き継ぎ、元workspaceの表示名を移動先名へ上書きしません。

元workspaceのcontrolとsidebar位置はcommitまで保持します。native adoptionや元containerの切り離し中に失敗した場合は、両方のcontrol tree、元のsidebar位置と選択を復元してからエラーを表示します。元containerの破棄はlive paneが既に移動先へ移り、元のペイン一覧とcontrolが空になった後です。通常closeとcontainerの切り離しを共有し、closeが持つshell終了処理を移動に使いません。

移動先のidentity、現在のsource選択、生存・ownerと保存depth境界は#22と同じ経路で再確認します。閉じたsourceの古い行から別ペインを動かしません。移動先がない場合は無効です。「新しいworkspaceへ移動」はsplit限定を維持します。

保存は既存version-1のlayout、CWD、browser URL、選択だけです。移動だけではファイルを書かず、次の明示save・通常closeに空workspaceを残しません。workspace番号は移動だけでは進めません。履歴、shell/browser memory、agent状態や新しい設定は保存しません。

## 検証の範囲

test-only `65ad93c3b39ef0338f62dc2083728cb57f158159`を同じfull suiteで[隔離Windows CI](https://github.com/cardamon04/fork-cmux/actions/runs/42428618261)に実行しました。実ConPTY/browser初期化後に、source1 pane・destination2 panes・有効なsnapshotをログで確認し、nativeメニューが無効という機能不足で失敗しました。コンパイル/setup失敗とは区別します。

`LastPaneTransferScenario`は両PowerShellとterminal/browserの4組をfull suiteへ組み込みます。nativeメニュー、入れ子の移動先への採用失敗と復元、表示済み行の並べ替え、live shell memory/CWD・browser Core/page/address focus、実BELの現在owner、空の元containerのみ破棄、stale行、既存schemaの明示save・通常close・再開、移動先なしを確認します。元のtest-only discriminator fileを維持して実装後も実行します。

#22の旧scenarioは「1ペインならこのメニューが無効」という期待だけを新しい仕様に合わせます。既存235件を残し、source-close isolationやbrowser address/depthの回帰を削除しません。実行したケース数・結果と未実行範囲、独立レビュー、最終HEADのCIをPR #23で確認します。localではcompileのみで、native app/testは起動しません。

過去の`DETACH_STATE` timeoutは未帰属です。今回のfull suite成功や下記Swift修正だけで原因解明・修正済みとは扱いません。実PC GUI・physical入力・OS IME/clipboard、pwsh表示差、#14 paint差、履歴仕様回答、4agent/10pane・1営業日の製品受入は引き続き未完です。

## 本家の追加差分

開始前fetchは2026-11-07 23:59 UTC、本家mainは`40455a2f4326d030f52b158684a0c48242b568ca`です。前回`be5e2433`から6 commits / 19 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み146 commitsです。baselineを進めず、fetchを統合済みとは数えません。

- [`MainActorTaskStore`のteardown](https://github.com/manaflow-ai/cmux/commit/40455a2f4326d030f52b158684a0c48242b568ca)は、SwiftUIの未使用`@State` default storeでregistryを作らず、lock外でweak参照の解放・task cancelを行う修正です。WindowsはこのSwiftUI/store経路を使わず、そのまま適用するコードはありません。過去のConPTY timeoutの原因と混同しません。
- 他5件はnightly canary/smoke/rescueのfeed、risk履歴の省略、merge freezeのGraphQL読取、hosted-lightの読取上限、CEF/R2配布です。WindowsのC#/xterm移動処理と直接共有せず、一括取り込み・配布・CI設定変更は行いません。既存のGit内部BUG、不足branchやMac compile上限が解消したとは記録しません。

open PRも症状で検索しました。[#37735](https://github.com/manaflow-ai/cmux/pull/37735)は`feat-cmux-next`のtab/pane animationとdaemon側のfocus topologyを扱います。Windowsにはその層がなく、外部PRの実装は流用していません。完成判定前にmainを再取得し、追加差分をPRへ記録します。
