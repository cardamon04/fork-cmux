# Windowsの既存ワークスペースへのペイン移動

F-01の未実装部分として、分割内のlive terminal/browserを既存workspaceへ移動します。baseは未マージのPR #21 `32fcbedf8c1b4354b78ba2e22bddb052813199d9`です。BLOCK中の#20を含まず、URL修正の上限・追加指示待ちは維持します。

## 操作と境界

「既存ワークスペースへペインを移動」は他のworkspaceを一覧表示します。行にはsidebarの位置と名前を表示し、同名のworkspaceも区別します。名前の`&`やUnicodeは文字として扱います。行を選ぶと、移動先の選択paneの右へ分割を作って元のpaneを移し、sourceのsplitを残りのpaneへたたみます。browserは同じpage/controllerを保ち、自分のaddress fieldを選びます。

表示した行はsource paneとdestination workspaceのidentityを保持します。sidebarの順序が変わっても別のworkspaceへは移しません。sourceの選択が変わった場合、source/destinationが閉じた場合、終了中の場合は何もしません。単一paneだけのsourceは今回の両移動操作を使えず、workspace全体の移動は残要件です。

移動先の選択paneが既存の保存深さ上限に達している場合は、その行を無効にし、別のpaneを選ぶ案内を表示します。クリック経路でも同じ条件を確認します。深さの値は保存validatorと共有し、schema・上限自体は変更しません。一般のSplit/New workspaceが別の保存上限を超える既存経路を、この対応で解消したとは扱いません。

既存の「新しいワークスペースへペインを移動」とtransactionを共有します。成功まで元のsplitを保持し、native adoption失敗時は移動元・移動先のcontrol treeとlive resourceを戻してからエラーを表示します。ConPTYには中間の小さなviewportを反映せず、確定・rollback後のlayoutを反映します。変更された現在のownerを通知・CWD・focusで使います。

移動後のbrowserのaddress fieldが狭いpaneのnavigation overflowへ入る場合は、そのoverflowを開いてからfocus・全選択します。page/controllerの再作成はしません。

保存は既存version-1 schemaによるlayout、directory、browser URL、選択だけです。移動だけでは保存せず、明示saveと通常close/reopenで反映します。shell/browser memory、command/output、履歴期間の追加保存は行いません。新しいshortcut、CLI/API移動、別windowへの移動、drag/drop、tab stripは残要件です。

## 本家追従記録

開始前のfetchは2026-11-07 00:12 UTC、本家`manaflow-ai/cmux:main`の`be5e24339c6eabb8e929a7029c4e6f834e257c97`です。前回の`407c048ae45c2e8ae0694ed1a09390c70033a6ca`から31 commits / 61 paths、固定baseline `2e4c17c63767048f6a790b071f87e7eca68ff8ae`から未取り込み140 commitsです。fetchを取り込みと扱わず、baselineを進めません。

- [Ghostty right-click-action](https://github.com/manaflow-ai/cmux/commit/4616426077028cf9edc35d5aaf9014d68d5f7811)は、context-menu以外の設定をGhosttyが処理した場合にcmux menuまで開く問題を修正しています。WindowsはGhostty設定・surface mouse actionをまだ組み込んでいません。F-02/F-11導入時にcontext-menu、paste/copy、modifier、mouse-upの組合せを比較する対象へ残し、修正済みとは扱いません。
- [Cloud welcomeの非有限値](https://github.com/manaflow-ai/cmux/commit/a62cc24518413c2ddb4fd07e733a667a34322058)はprogress railとplayhead rateのNaN/無限値を扱います。WindowsにはCloud welcomeの同じ経路がなく、F-12の導入前の検証項目です。
- [sign-upの拒否理由](https://github.com/manaflow-ai/cmux/commit/074abb874d7fa053161686a9bd1e4ec965501f83)は既存emailの秘匿と安全なerror code表示です。Windowsの認証画面は未実装で、web/backendのコードや認証設定は変更していません。
- 残る28 commitsはCI、nightly復旧、runner選択・queue、real-login canary、Computer Use helper/SDKの署名・公開等です。今回のWindows移動経路と直接の共有コードはなく、一括取り込み・workflow/署名/権限変更は行いません。既存forkのGit内部BUG、不足branch、fast guard/Mac compile上限が直ったとも扱いません。

実装前に本家のopen PRを`move pane workspace`と`"existing workspace" in:title`で検索しました。外周split・window移動などの別機能があり、後者の完全一致候補はありませんでした。Windows側の既存move-to-new-workspace transactionを拡張し、外部PRの実装は採用していません。

## 検証と残作業

test-only commit `5c1c1fedc470400d23edf3a1919065334b6f2eb9`は製品操作を追加せず、実ConPTY・native menu/control・browser fixture・保存復元・adoption失敗を扱う回帰を追加します。既存のfull suiteを隔離Windows CIで実行し、準備失敗と機能の失敗、実装後のgreenを分けて記録します。最終HEAD・実行結果・独立レビュー・完成前の本家再fetchはPR #22に記録します。localではcompileのみで、native app/testを起動しません。

新しいWindows UI文言を9言語のcatalogに追加し、既存72 keysの全値を保持します。destinationの書式は既存のworkspace名とordinalだけの共通書式です。localization entrypointとstrict catalog検証に加え、Windows JSONのkey・引数・値の保持を検証します。

最初の実装`d1c3f896af8ed5390bc795f3ec71c68f7d95af54`の[実行](https://github.com/cardamon04/fork-cmux/actions/runs/42318713409)は、native row/targetとlive terminalの2ケース後にbrowserのaddress focus・selectionのassertionで失敗しました。independent reviewの元source/規則座標を保持し、このP2への修正候補を検証します。保存深さの指摘は別のnative fixtureで扱います。最初の`f94a47c2`はrestore中のzero-size splitter設定で準備失敗し、移動・depthの再現には数えません。`683ce8d3`はdepth16/17 leavesを保って5%のsliverへ前提を修正したtest-only HEADです。各結果と製品修正回数、修正後の同じdiscriminatorはPRに記録します。

実PC GUI・実OS IME/clipboard、physical input、pwsh表示差、#14 paint差、未回答の履歴仕様、4agent・10paneで1営業日の受入は未解決です。今回の移動操作だけでF-01全体や製品全体を合格にしません。
