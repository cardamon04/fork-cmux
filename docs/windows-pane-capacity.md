# Windowsのペイン追加と保存上限

F-01/F-10の保存整合性を修正します。version-1の保存形式は全workspaceの合計ペイン数を256個に制限しますが、workspace作成と通常のterminal/browser分割には同じ判定がありませんでした。257個目の追加でlive layoutを保存できなくなります。未実装の機能追加より、既存layoutの保存可能性を失う経路を優先します。baseは未マージの#25 `2d32596da219ad03c670607381a4493bbcd3c0fb`、成果・独立レビュー・最終CIは[Draft PR #26](https://github.com/cardamon04/fork-cmux/pull/26)に記録します。BLOCK中の#20は含みません。

## 追加と移動の判定

`SessionSnapshot.MaxPanes=256`を保存検証と追加判定で共有します。追加できるかは現在のworkspace群のペイン数から求め、別の可変カウンターを持ちません。通常workspace作成、空状態からのbrowser workspace作成、terminal/browser分割とnative actionが同じ判定を使います。拒否はresource確保・採番前に行います。closeで1個空けば再び追加できます。

移動は既存ペインを再利用するため、256個でも新workspaceへの分離と既存workspaceへの移動を維持します。workspace件数64・採番2147483646と深度16の既存判定はそのままです。保存schema、履歴やshell memoryの保存範囲、1MiBのbytes上限は変更しません。bytesの操作時境界は未完了です。

## 修正前の実行証拠とfixtureの範囲

[test-only `1d139a53ce69dc93cdd9cc28515a86f061046e9d`](https://github.com/cardamon04/fork-cmux/actions/runs/42935258213)で、両PowerShellのworkspace作成と縦分割が、保存済みの有効な256ペインから257ペインへ増え、その直後の保存検証が失敗しました。4個のprecondition/action/failureを原ログで確認し、実checkoutのtreeも候補と一致しています。

初回[test-only `fba4da88`](https://github.com/cardamon04/fork-cmux/actions/runs/42933582252)は、横方向だけのbalanced model treeの幅が不足し、既存`WorkspaceView.ApplyRestoredRatios`でSplitterDistance例外となりました。対象操作に到達せず、容量超過の証拠には使いません。モデル配置を縦横に分散し、選択前のgraph検証と選択後の実split寸法の前提を追加した別コミットで再現しました。容量を確かめる操作とassertionsは初回から維持しています。この配置補正は、既存の小寸法ratio適用例外を修正・否定するものではありません。通常利用経路の再現は未確認の課題として残します。

fixtureは実際のnative control treeと製品の保存処理を使います。workspace作成側は1個の実terminal・1個の実browserと254個の未起動browser model controls、分割側は255個から最後の分割を行い、2個の実terminal・1個の実browserと253個の未起動モデルになります。実shell memoryの確認は呼び出しごとの固有マーカーを使い、以前の出力を再利用しません。

修正前と同じfull-suiteコマンドを修正後にも使います。

```powershell
& windows/Cmux.Desktop.Tests/bin/Debug/net10.0-windows/Cmux.Desktop.Tests.exe desktop-results
```

通常workspace・browser workspace・縦横分割・browser分割の拒否、native actions、既存/新workspaceへのlive移動、close後のterminal/browser追加、既存schemaでの保存・通常終了・store読み戻しを調べます。256 live engine起動・256ペインのGUI復元・負荷受入は意味しません。ローカルはcompileのみで、native app/testは実行しません。実行した件数・失敗・未実行はPRに記録します。

実PC GUI/physical入力/実OS IME・clipboard、pwsh表示差、#14 paint差、履歴範囲・期間の本人回答、4agent/10pane・1営業日の受入、過去DETACH_STATE、#23のlate source-removal/nonzero-index rollback等は未解決・未検証のままです。旧監視のOS状態も不明のままで、CIM再照会・代替列挙・監視追加をしません。

## 本家mainの評価

2026-11-11開始前の取得は`0718958d20e3321c223d3e9182c11d067fc8e5bd`です。前回`a2e846437dbb5f2b31a8ae55f991bc5e828d2280`から42 commits / 234 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み258 commitsです。既存の追跡ツールで記録し、完成前にも再取得します。fetchを統合済みとは扱いません。

- 11件はTUI実装・テストです。[title/BELのlock外での発行](https://github.com/manaflow-ai/cmux/commit/22636cb4359f9b0b2a2b4427531e6435742f1fa3)はRustのterminal/geometry/Mux lock順序、[flood時のjournal gapとhost接続維持](https://github.com/manaflow-ai/cmux/commit/901d0f01956812a234c6896a161d7e2b090bbc6f)はdaemon tapとjournal ingressの契約を確認しました。WindowsのWebViewメッセージ・直接ConPTY・bounded renderer channelはこれらを使いません。lossy history処理を移植したり、過去timeoutを解決済みにしたりしません。attach-aware reaper、startup capability、nested Claudeのsession/PID・clear/resume・hook budgetも今後の比較対象です。
- 2件はMacアプリ/CLIです。[left-sidebarの操作と対象指定・focus契約](https://github.com/manaflow-ai/cmux/commit/3abdc7a68030035e40cbd49362d40134381a0da3)、[設定Claude実行ファイルごとのmobile model cache](https://github.com/manaflow-ai/cmux/commit/0b458fe00490d72a680939e77be4f59e82bec060)を確認しました。WindowsのCLI/socket・agent/mobile統合は未実装で、今回の容量修正に対応済みとして含めません。
- Cloud Enterprise/operator member管理1件、Web marketing/provider分離2件、iOS demo onboarding/配布1件は、その領域の比較対象です。現在のWindows経路に直接の互換patchは確認できませんでした。Cloud権限・料金・account・pairing・配布や設定を変更せず、server codeやassetsは流用しません。
- 25件はCI/build/reviewです。rescue・runner・publisher・merge gate・test工具の変更を取り込みません。TUIの70分上限や個別テスト180秒は別laneの契約で、既存Mac compile75分・Windows12分の上限を変えません。Git内部BUG、不足fork branch、Mac compile制限の解消とは扱いません。

本家open PRを症状で検索し、#22109/#24430/#25764の変更先も確認しました。Macの512-panel記録・保存復元の扱いで、Windowsのこの256ペイン判定と重複する実装は確認できませんでした。検索は網羅的とは主張せず、外部実装を流用していません。
