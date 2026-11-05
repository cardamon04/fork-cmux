# Windows未読通知一覧と本家差分

F-04のうち、BELで未読になったlive terminalを一覧から選ぶ操作を実装します。baseは未マージのPR #19 `8b710d68fcde73ee9c508ee3c8152b52c62c7da9`です。PR #20のリンク実装やP1/P2修正を含まず、その追加指示待ち・修正回数上限を維持します。

## 操作と範囲

「未読通知」はワークスペース名と端末の位置を示すnative menuを開きます。端末タイトルはtooltipとaccessibility descriptionに残します。空の場合は操作不能の案内を表示します。行を選ぶと対象の現在のworkspaceへ移動し、実際に端末へfocusしたときにその未読状態を消します。「次の未読ペイン」と選択経路を共用します。

一覧を開いた後に別のBELが到着しても、既に表示した行の対象は変わりません。更新は一覧を開き直したときに行います。閉じたpane、既読になったpane、終了中のwindowへの行操作は何もしません。pane移動後は古いworkspaceを使わず、現在のownerを確認します。

保存対象・schema・通知保存期間は増やしません。通知本文、既読履歴、agent hook/CLI、toast、音、phone転送、本家のmodifier/digit操作は残要件です。今回の一覧だけでF-04全体を合格にしません。

## 本家追従記録

2026-11-04 23:42 UTCのreportで、実fetchした本家main `c9ed9bd7078e439efc1e09e50cd5577af33ee516`を確認しました。前回の`f8ecf833b366de37fb2d4e4212cd7d7ef82c4f5b`から38 commits・137 paths、初期baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み104 commitsです。baselineを進めず、fetchをmergeとして扱いません。

- [通知popoverの行操作](https://github.com/manaflow-ai/cmux/commit/1730c03f85cffae11b40794fb5ebbe2bedc8107c)は、表示した行と後着通知を分け、表示済みの対象を開くこと、windowごとの所有関係、hintとaccessibilityが対象です。今回のWindows一覧も表示済みpaneの同一性を保ちます。新しいdigit shortcutや本家の全popover機能を移植したとの主張はしません。
- [browser screenshotの出力先保護](https://github.com/manaflow-ai/cmux/commit/2ec620bd1a56e0b074de74a74c16ae764a244c20)は、Swift CLIがdirectoryを削除する問題と、失敗時に既存fileを保つ修正です。Windows製品のCLI/screenshot出力経路は未実装で、この修正も未取り込みです。F-05/F-07実装時に同じdirectory保持・既存file保持・base64 fallbackを検証する対象として残します。
- [Chiefのrelease flagとTLS修正](https://github.com/manaflow-ai/cmux/commit/c9ed9bd7078e439efc1e09e50cd5577af33ee516)は、Chiefの各入口・保存復元・CLI helpの制御と、Rust iroh relayのsystem CA利用です。WindowsにはChief・Rust Cloud relayをまだ組み込んでいません。flagを有効化したり、OS trustや接続設定を変更したりせず、F-12/F-16導入前の比較対象に残します。
- [SSH clipboardの共有trust](https://github.com/manaflow-ai/cmux/commit/0ad82b815901c07b3a01517ecd88ee96ff7502b6)、[Pi reload通知](https://github.com/manaflow-ai/cmux/commit/70121187cb2a82ccb9838b81b5cbaf961d2fafd0)、[Kiro hook判定](https://github.com/manaflow-ai/cmux/commit/15856e729bfdbbd1d07acd2489b3acbe807d7404)は未実装のSSH/agent連携に関係します。実OS clipboardやユーザーのhook設定を変更しません。
- その他はCIのadmission・rescue・API budget・token選択・test-red parser・workflow登録と、iPhone App Store画像です。既存forkのGit内部BUGや不足branchが直ったと扱わず、設定や権限を変更して成功に見せません。

## 検証の扱い

test-only `a4bfa355b3e3abb8c2d1816f085c842dbafb0f59`の[Windows実行](https://github.com/cardamon04/fork-cmux/actions/runs/41937085614)は、ビルドと実ConPTYの準備後に`The native unread notifications list is missing.`で失敗しました。setup失敗ではありません。製品側の実装後も、同じfull suite内のnative menu検証で空状態、後着BEL中の表示済み行、対象だけの既読化、開き直し、閉じた対象を確認します。既存のNext unread・非選択window・移動・browser・通常復元の検証は残します。

9言語の表示文言と引数を検証し、既存68 keysを保持します。`NotificationTarget`は二つのラベルと区切りだけの共通書式で、未翻訳の英文ではありません。最終HEADの実行結果、独立レビュー、未実行範囲はPR #21に記録します。localではコンパイルのみ行い、native GUIは既存の隔離されたWindows CIで検証します。実PC GUI/IME、OS clipboard、pwsh表示差、#14 paint差、履歴仕様は解決済みにしません。
