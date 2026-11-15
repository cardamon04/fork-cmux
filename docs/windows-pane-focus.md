# Windowsの次／前のペインへの移動

F-01のフォーカス移動を日常利用へ進める独立部分です。baseは未マージ#29 `10271ecef31cf4fffa05752de9986e7f5aaf4603`。#20/#28 BLOCKと閉鎖済み#27を含みません。結果は[Draft PR #30](https://github.com/cardamon04/fork-cmux/pull/30)に記録します。製品全体やmainで使える完成版とは扱いません。

## 操作と比較

「前のペイン」「次のペイン」は、選択workspaceのlive split-treeをPanel1（左／上）、Panel2（右／下）の順で進み、端で循環します。生成順ではありません。現在のtreeと所有pane集合から毎回対象を解決し、close/split/move後の古いpaneを選びません。1ペイン・対象不在は移動しません。terminal/browserは既存FocusPaneとActivatePaneを使い、選択、CWD/caption、action、未読の既存経路を共有します。新しいshortcut/API、保存field・履歴範囲、認証や設定は追加しません。既存ActivePane保存に従います。

本家main `7adfd62de12b545a195cea34688b55d4895006ff`の[cycleFocus](https://github.com/manaflow-ai/cmux/blob/7adfd62de12b545a195cea34688b55d4895006ff/Sources/Workspace%2BSurfaceNavigation.swift)、[PaneCycleNavigator](https://github.com/manaflow-ai/cmux/blob/7adfd62de12b545a195cea34688b55d4895006ff/Packages/macOS/CmuxPanes/Sources/CmuxPanes/Model/PaneCycleNavigator.swift)、DockのcycleDockPaneFocusを比較しました。live集合・現在pane・2ペイン以上を確認して、ツリー順に循環する契約です。方向指定・focus history・zoom・タブ移動の互換全体は未完了です。

本家のopen PR症状検索は各20件の上限に達し、網羅的ではありません。[PR #1611](https://github.com/manaflow-ai/cmux/pull/1611)の本文・変更先を確認しましたが、別タブを全ペイン横断する追加shortcut/座標順の操作です。今回は既存mainのpane cycleをWindowsへ対応させ、外部PRのコードは採用していません。

## 再現と検証

同じ既存Windows laneで次のfull-suiteを使います。

```powershell
& windows/Cmux.Desktop.Tests/bin/Debug/net10.0-windows/Cmux.Desktop.Tests.exe desktop-results
```

初回81c50367はCWDを報告するPowerShell統合をテストが使わず、2件ともsetup timeoutになりました。65 printed passes・最終reportなしで、機能欠落のredや従来362件保持の証拠にはしません。test-ready c7502275は既存PowerShellArgumentsによる起動へ修正し、実terminal/browser準備後のnative操作を判別します。以後の製品実装前後で21件を固定し、red/greenの実行・checkout tree・最終reportをPRで確認します。初回81c5とfixture全体のbyte一致は主張しません。

2種のPowerShellごとに、実terminal/browser、生成順と異なるsplit tree、次／前の循環、host toolbarからのnative focus、browser document状態、選択出力と未送信Unicode入力、workspace分離・1ペイン無効、close/split/move後の対象更新、既存ActivePaneの保存・JSON roundtrip、所有shell/profile解放を確認します。各10件と9localeの1件が新規21件です。従来362件保持と最終件数は実結果で判定します。今回の追加試験はGUI再起動復元を実行せず、既存保存fieldのroundtripまでです。

private MemoryClipboardと所有loopback fixtureを使い、実PCの入力・clipboard・画面は操作しません。ローカルはcompileのみでnative app/testを起動しません。物理toolbar/overflow、OS IME、全geometry・手動selection形式・全protocol、4agent/10paneの1営業日受入は未実施です。

## 本家追従と継続する制約

開始時に本家mainを取得し、前回bb1c79a9以降8commits/22paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み298件を既存ツールで記録しました。全8件のpath/metadataと選んだ4件の小さいproduction差分を確認し、全テスト・全行の実行や精査とはしません。完成前の追加取得・評価は同PRに記録します。

今回の差分はCI/iOS配布です。owned Mac優先・rescue/runner分類、iOS App Review gate、拒否HTTP読取りのretry、cancelled gateの後続実行への引継ぎ、nightly build slotとpost-feed tailの区別などを比較しました。現Windowsに必要な直接互換patchは特定せず、上流の240→360分への拡大・retry・runnerや配布設定を取り込みません。過去の拒否操作や同HEAD CIの再試行の新許可とは扱いません。

#20の修正2回上限、#28のDETACH_STATE帰属・従来345件保持・checkout tree未確定、#23の保留、履歴範囲・期間の回答待ち、実PC GUI/OS IME/clipboard、pwsh/#14 paint差を維持します。旧commit723955e2…照会とMac job130899307499/logsは本人回答なしに再試行しません。今回もMac compileログは要求せずmetadata/annotationを使い、build summary有無・compile完了の未確認を区別します。merge/deploy/CLA/権限・設定・CI上限やcheck変更は行いません。
