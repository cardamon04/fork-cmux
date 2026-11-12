# Windowsの折り畳み保存分割の復元

F-01/F-10の既存の保存・復元を修正します。別機能を増やす前に、有効な保存レイアウトを開けない既知の候補を実際の復元経路で確認しました。baseは未マージの#26 `6c1acda85831c777a40357cf0cf25c0642f54a92`です。BLOCK中の#20と、browserキャンセルの仮説が再現しなかった閉鎖済み#27は含みません。最終HEADの結果・独立レビュー・CIは[Draft PR #28](https://github.com/cardamon04/fork-cmux/pull/28)に記録します。製品全体の完成ではありません。

## 修正前の事実

version-1の有効な保存graphを実SessionStoreに書き、DesktopWindowを通常の復元経路で起動します。3つの実browser pane、深さ2の同方向splitで、親比率0／1により左右どちらかの子splitを折り畳み、子の保存比率を0.37にします。縦横と左右の4条件です。

[診断用test-only `0b5c432a4580175a57a33900688ec24c739f20bc`](https://github.com/cardamon04/fork-cmux/actions/runs/43130679417)で4条件とも`ApplyRestoredRatios`中のnative `SplitterDistance`のInvalidOperationExceptionを捕捉しました。保存したbrowser graphの代わりに新規Terminalが開き、保存禁止の状態になります。元の保存bytesは4条件とも維持されました。未捕捉のアプリ異常終了や保存ファイル破壊とは扱いません。最初のテストはフォールバック後のcastで失敗したため、診断を追加した別commitで例外・復元状態・元ファイルを明示しています。最後の診断以降、保存graphと操作・assertionは修正前後で変更しません。

## 幅が確保されるまで比率を保持する

splitの軸長からsplitter幅を引き、両panelの最小幅を満たせる場合だけ保存比率をnative distanceに投影します。幅が不足する間はPendingRatiosに保持し、保存時にもその値を使います。所有するsplitのSizeChangedで再試行し、適用成功・split破棄・workspace破棄時にイベント購読を解除します。再入を防ぎ、pane移動中の不完全な子構成には触れません。非表示workspaceは通常の選択時に再適用します。

保存schema、workspace64・pane256・depth16・1MiBの上限、history/command/outputの保存範囲、各国語catalog、CI設定・時間上限は変更しません。継続的な比率固定やrenderer全体の再設計ではありません。

## 検証の範囲

修正前と同じfull-suiteコマンドを既存の隔離Windows CIで実行します。4条件ごとに、保存graphと選択の復元、折り畳み中の比率と元ファイル維持、親split拡大後の比率とbrowser Core同一性、実Save layout・通常close・store再読込とprofile解放を確認します。最後の診断のテストは固定し、実行結果をPRに記録します。ローカルではcompileのみを行い、native app/testは実行しません。

このfixtureは3 browserを使う復元回帰です。256 engineの起動、全geometry、実PC GUI/physical入力/実OS IME・clipboard、4agent/10paneの1営業日受入は示しません。pwsh表示差、#14 paint差、履歴範囲・期間の本人回答、過去DETACH_STATE、#23のlate-removal/nonzero-index rollback、Macコンパイル完了等の未確認項目を引き継ぎます。

## 本家mainの差分評価

2026-11-12開始時の本家は`a9f38e61368890e6765861ed818869d52c87310a`です。前回の`6f2ac5bba01a76a167d66d3a5ee99a201d47b52d`以降13 commits / 45 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み275 commitsを既存追跡ツールで記録しました。完成判定前にも取得します。fetchを統合と扱いません。

TUI 6件はUnix子processのexec前判定、handoff時のterminal維持、scoped attachのmouse/resize、agent hookの時刻順序・SessionStart budget、usageテスト同期です。現在のWindowsの直接ConPTY・WebView2・BEL経路には同じdaemon/hook/attach所有権graphがなく、将来の比較対象として残します。CI 6件はrunner予約・lint ratchet・run名・libtest watchdog・no-fail-fast・base側境界guardです。WindowsのC#は当該lint対象外で、既存Mac/guard制限の解決とはしません。Web配布1件はLinux導線と既存署名feedの区別で、Windows配布実装は加えません。13件のpathと選定したproduction契約を確認し、全変更行の精査・全テスト実行とは主張しません。現在のWindowsへの直接互換patchは特定できず、一括取り込み・設定変更は行いません。
