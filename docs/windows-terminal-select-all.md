# Windowsの端末出力全選択

F-02の選択・コピーを進める独立単位です。baseは未マージ#26 `6c1acda85831c777a40357cf0cf25c0642f54a92`。BLOCK中の#20/#28と閉鎖済み#27を含めません。最新の本人回答はなく、これらの照会・修正を再開する追加許可として今回の定期実行を扱いません。最終HEADの実行・独立レビュー・CIは[Draft PR #29](https://github.com/cardamon04/fork-cmux/pull/29)に記録します。

## 操作

「出力をすべて選択」は選択中のterminalの保持出力を選び、その後の明示的なCopyでclipboardへ書きます。全選択だけではclipboardに書きません。Findの入力を閉じて選択の所有権を端末に戻し、検索欄の文字列を誤ってコピーしないようにします。未送信のshell入力、隣や別workspaceの選択は保持し、browserでは無効です。既存toolbarと標準overflowに同じactionを使います。Ctrl+Aなどの新しいshortcutや設定・保存fieldは追加しません。保存履歴は未回答のままです。

比較対象は[Ghosttyのselect_all action](https://ghostty.org/docs/config/keybind/reference#select_all)です。Windowsでは既存のpinned xterm APIを使い、保持範囲（scrollback 5,000行と現在画面）に限定します。全履歴・全Ghostty設定互換とはしません。open upstream PRの症状検索2件は結果0でしたが、網羅性は主張せず、外部実装を流用していません。

## 修正前と同じ判別テスト

[test-only b80d2c524199d9da2693579e8688c27e822d86ac](https://github.com/cardamon04/fork-cmux/actions/runs/43535695175)では、実PowerShell7/Windows PowerShell5.1の起動・ready prompt・local rendererの前提が成立した後、native actionがないという同じassertionで2件とも失敗しました。実checkoutのtreeとtest-only treeは一致しています。setup失敗や後続未実行をred・成功と取り違えません。

既存隔離Windows laneで、修正前後に同じfull-suiteコマンドを使います。

```powershell
& windows/Cmux.Desktop.Tests/bin/Debug/net10.0-windows/Cmux.Desktop.Tests.exe desktop-results
```

2種のshellごとに実scrollback・Unicode出力、明示Copy、Find query、未送信Unicode入力とEnter後の実行、pane/workspace/browser分離、所有shell/profile解放を確認します。各6件と9localeの1件が新規13件です。private MemoryClipboardを使い、実OS clipboardは操作しません。診断後のテスト・操作・assertionは固定し、最終件数と従来345件保持は実行結果で判定します。ローカルはcompileのみでnative app/testを起動しません。physical input、標準overflowでの実マウス操作、OS IME、全geometry、4agent/10paneの1営業日受入は未実施です。

## 本家mainの評価

開始時`bb1c79a9e134dc31d5d329719b681ecb85627b66`を取得し、前回`af4309a9`から14 commits / 70 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み290 commitsを既存追跡ツールで記録しました。14件のpath/metadataと選定したproduction差分・契約文書を確認し、全行精査・全テスト実行とは主張しません。完成前にも取得し、最新の追加差分はPRに記録します。fetchを統合と扱いません。

TUI1件はhook順序をper-boot monotonic clockに改める契約です。同値はjournal順、新bootは適用、stampなしはjournal順、遅延errorは順序に従い、lifecycle/dialogは適用、stampはrestart後も保存します。WindowsのBEL一時状態に同じhook/journal経路はなくF-04/F-09/F-10の将来比較です。Web2件はclient IDなしでは無効なChatGPT sign-inとdeploy取消理由の説明で、Windows認証・配布実装にはなりません。CI11件はnightly promotion/2時間stale警告、CLA retry backoff、署名queue、未link mergeの厳密照合、TUI timeout retry/full-run、advisory境界、iOS夜間登録等です。既存の再試行禁止・5分/75分上限・チェックを変更せず取り込みません。現在Windowsへの直接互換patchは特定できませんでした。

## 残る検証

PR #28のDETACH_STATE帰属・従来345件保持・checkout tree未確認、実PC GUI/OS clipboard/実IME、pwsh表示差・#14 paint差、履歴範囲/期間、PR #23の未検証、#20 BLOCK2回上限、Mac compile完了等を引き継ぎます。拒否されたcommit723955e2…照会とMac job130899307499/logsは本人回答なしに再試行しません。新しいCIもMac生ログは要求せずmetadata/annotationで確認し、summary有無は未確認として扱います。merge/deploy/CLA署名/権限・設定変更は行いません。
