# cmux Windows版の要求と引き継ぎ

自分の普段の開発で使えるcmuxのWindows版を目指します。本家の全機能対応、クラウド・iPhone連携、最新機能への追随、個人用winget定義による導入、アプリ内自動更新が対象です。要求・要件は確定済みで、Windows版の実装・動作検証は未着手です。

要求・要件と進行状況の正本は[requirements.md](requirements.md)、全15問の回答記録は[interview-log.md](interview-log.md)です。

## リポジトリ

[cardamon04/fork-cmux](https://github.com/cardamon04/fork-cmux)は、[manaflow-ai/cmux](https://github.com/manaflow-ai/cmux)の公開フォークです。既定ブランチはmainです。

本家のREADMEを維持し、この案内、要求・要件、回答記録をdocsに保存します。

## 別セッションでの再開

このリポジトリを開き、次のように依頼してください。

> docs/windows-port.mdとdocs/requirements.mdを読んで、cmux Windows版の作業を引き継いでください。確定事項を引き継ぎ、必要な技術検証と設計を整理してください。回答の原文が必要な場合はdocs/interview-log.mdを参照してください。

要求・要件の確定状態、完成条件、技術検証が必要な事項を確認し、回答済みの質問を繰り返さずに続けてください。過去に提示した技術案は、ユーザーが採用した方針として扱わないでください。

## 次に確認すること

1. フォークしたWindows版から、本家クラウド・公式iPhoneアプリへ接続できるか。
2. 最新の本家mainを基準とする全機能の対照表と、Windows向けの同等機能の検証方法。
3. 本家の継続的な更新取り込みに適した構成と、既存Windows派生版からの流用範囲。

未対応機能を残したまま完成とは判断しません。クラウド・iPhone連携に制約が判明した場合は、要求・要件に定めた手順で範囲を再相談します。
