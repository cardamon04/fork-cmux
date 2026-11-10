# Windowsのworkspace作成と保存容量の整合性

F-01/F-10の既存動作を修正します。version-1の保存形式はworkspaceを64個、採番値を2147483646までに制限しますが、通常作成と新workspaceへのpane移動には同じ判定がありませんでした。65個目、または最後の番号を使った後の作成でlive sessionが保存できなくなります。新機能より、この保存可能性を失う経路を優先します。baseは未マージの#24 `aa8962fff998ca6390f50f6c7e462d064259d84e`、成果と最終検証は[Draft PR #25](https://github.com/cardamon04/fork-cmux/pull/25)に記録します。BLOCK中の#20は含みません。

## 共有する判定

`SessionSnapshot.MaxWorkspaces`と`MaxWorkspaceSequence`を保存検証・作成判定で共有します。通常作成、空状態からのbrowser作成、新workspaceへのlive pane移動は、件数・番号の空きがある場合にだけ進みます。native actionも同じ判定で無効になります。拒否時には番号、選択、既存paneと保存済みファイルを変更しません。closeで件数の空きは回復します。番号は巻き戻したりリセットしたりしません。

既存workspaceへの移動は、新しいworkspaceを増やさないため維持します。移動元が空になれば、そのcontainerだけを破棄して件数が減ります。現在のworkspace内のterminal/browser分割も、従来の深度判定で許可します。schema、保存対象と履歴、全体pane数256・1MiBのbytes上限は変更しません。これら別の容量境界の操作時検証や、本家全機能との同等性は未完了です。

## 再現と検証の範囲

test-only `a1c43675de7443b7be20462d616c5fc526de9c1d`の[Windows CI](https://github.com/cardamon04/fork-cmux/actions/runs/42730000725)で、両PowerShellに有効な保存状態を作り、製品の通常作成を直接呼んで両方の不具合を再現しました。件数は64→65、採番は2147483646→2147483647となり、いずれもその直後の保存検証が失敗しました。compile/setup失敗とは区別します。

件数のfixtureは実際のnative control treeに1個の起動済みterminal、1個の起動済みbrowser、62個の未起動browser model controlsを配置します。64個のgraphを製品の保存処理でcheckpointし、作成拒否、native action、実terminalの分割、close後の作成と移動、同じshell/pageの維持、通常終了後のstore読み戻しを調べます。64個のlive engine起動・GUI復元・負荷受入試験を意味しません。

採番のfixtureは小さい実shell/browser構成です。最後の有効な番号を通常操作で消費し、その後の拒否とnative action、番号を増やさないbrowser分割・既存workspaceへの移動、保存・通常終了・実GUI再開を確認します。修正前と同じdiscriminatorを維持し、full-suiteコマンド `& windows/Cmux.Desktop.Tests/bin/Debug/net10.0-windows/Cmux.Desktop.Tests.exe desktop-results` を修正後にも実行します。最終件数と独立レビュー、各CIの成否はPRに記録します。ローカルはcompileのみで、native app/testを実行しません。

対象PCのGUI、physical入力・実OS IME/clipboard、pwsh表示差、#14 paint差、履歴保存範囲・期間、4agent/10pane・1営業日の受入は未検証・未確定のままです。過去の`DETACH_STATE` timeout、#23のlate source-removal/nonzero-index rollback等を今回の検証で解決済みにしません。旧監視プロセスのOS状態は不明のまま維持し、CIM再照会・代替列挙・監視追加は行いません。

## 本家mainの差分評価

2026-11-10開始前の取得は`b35c36fcfe445b449e211e29d84652139bf47072`です。前回`9114f7aa937058b650c648e34629ca387bbe7b88`から31 commits / 130 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み215 commitsです。既存差分追跡ツールで記録し、fetchを統合済みとは扱いません。完成判定前にも再取得します。

- 12件はTUI実装・テストです。[pane.moveのplaceholder shell終了](https://github.com/manaflow-ai/cmux/commit/b14c8ac1298056d007b1ba75329f00fec2fff970)は、Windowsが空containerに同じlive controlを移す経路との違いを確認しました。[host応答のdemux](https://github.com/manaflow-ai/cmux/commit/ab84b64020f38c65eecec27748676f82e94f2bb8)と[終了時の応答待機](https://github.com/manaflow-ai/cmux/commit/4b619fd130c4e3e12bd3c658ca90518bd45c867e)はRustのUnix host socket/queueの修正で、WindowsのConPTY pipe/renderer bridgeはそのprotocolを使いません。直接の互換patchは確認できず、過去timeoutの原因と推定しません。agent readiness/start/resume/reaper等はF-05/F-09/F-10の製品統合backlogとして残します。
- 1件は[iOS onboarding](https://github.com/manaflow-ai/cmux/commit/b614a9)、1件は[Cloud entitlement/API](https://github.com/manaflow-ai/cmux/commit/b35c36fcfe445b449e211e29d84652139bf47072)です。pairingや認証・admin設定の変更は行わず、Windowsでの接続確認済みとは扱いません。
- 17件はCI/build/reviewです。workflow・pool・権限・時間上限を取り込まず、既存のGit内部BUG・不足branch・Mac compile制限を解決済みにしません。

本家のopen PRを症状で検索し、#29439/#19768/#24430の変更先を確認しました。Mac/Swiftの保存・選択・復元経路で、Windows C#のこの修正と重複する実装は確認できませんでした。検索結果は網羅的とは主張せず、外部実装は流用していません。
