# Team NVN Web Project

SOUND VOLTEX (音楽ゲーム) の交流チーム「Team NVN」のポータルサイト / イベント運営システムです。

チーム内で毎週開催しているスコアアタック企画 **WeePA** を軸に、それまで Google フォーム + 手作業で行っていた「選曲の募集 → 開催 → スコア集計 → 結果発表」の運営を Web + Discord で自動化しました。個人開発で、実際に本番運用しています。

🔗 **本番サイト**: https://team-nvn-server.com/

## できること

### 参加者向け

- **選曲提案** — 次回の課題曲をフォームから提案
- **スコア投稿** — リザルト画像を添えてスコアを投稿。画像から SCORE / EX SCORE を自動読取 (Claude API による画像認識) するので手入力不要
- **3つの参加部門 (Tier)** — ガチ勢向けの EX スコア部門から、スコアを問わず「自己ベスト更新」を競うカジュアル部門まで、実力を問わず楽しめる設計
- **ランキング / 開催履歴の閲覧** — 集計結果は自動で公開

### 運営向け

- **承認フロー** — 投稿された選曲・スコアは Web 管理画面または Discord 通知のボタン (承認 / 修正承認 / 却下) で処理
- **イベントの全自動運行** — 週次の開催枠を自動作成し、終了を検知して Tier 別の結果画像を Discord に自動投稿、一定時間後にランキングを自動公開
- **オンマチ (オンラインマッチング)** — Discord 上の対戦部屋を統合ボードから自動生成・入退室管理する Bot
- **MSC イベント** — 複数曲・通しプレー形式のイベントにも対応 (アーケード / コナステ両対応)

## 技術スタック

| レイヤ | 採用技術 |
|---|---|
| フロントエンド | HTML / CSS / JavaScript (フレームワーク不使用) |
| バックエンド | C# / ASP.NET Core 8 + Dapper + Npgsql |
| データベース | PostgreSQL 16 |
| Web サーバー | Nginx (SSL 終端 / レート制限 / セキュリティヘッダ) |
| インフラ | Docker Compose on Ubuntu、Let's Encrypt |
| 外部連携 | Discord (Interactions + Bot API)、Anthropic Claude API (画像認識) |

## アーキテクチャ

```
[ブラウザ]
    ↓ HTTPS
[nginx]  リバースプロキシ / 静的配信 / SSL 終端 / レート制限
    ↓
[api]    ASP.NET Core — REST API / 画像処理 / Discord 連携 / 定期実行 (イベント終了検知)
    ↓
[db]     PostgreSQL 16 (Volume 永続化)

[scheduler]  cron — 週次イベント自動作成 API を定期実行
```

- 外部に公開されるのは nginx のみ。api / db は Docker 内部ネットワークに隔離
- DB への直接アクセスは想定せず、API を唯一の入口とする
- Discord Bot も専用経路を持たず、同じ管理 API を Basic 認証で叩く (ロジックの一元化)

## 設計ハイライト

**スコア投稿の全履歴を保持する複合キー設計**
スコアは「イベント × ユーザー × 投稿回数」の複合主キーで全履歴を保存。投稿回数はトリガーで自動採番し、同時投稿の衝突はアプリ側リトライで解決。カジュアル部門の「自己ベスト更新幅」などの集計は履歴から導出します。

**状態は派生させる**
承認ステータスは「負=終端 / 非負=前進」の規約で設計し、「非却下」を `>= 0` の一条件で表現。楽曲の「実施済み」も専用フラグを持たず開催レコードへの紐付きから導出することで、状態の二重管理を避けています。

**イベント運行の自動化**
API 内の毎分 tick がイベント終了を検知し、ランキングページを headless Chromium (Puppeteer) で画像化して Discord に自動発表。未処理の投稿が残っている場合は公開を保留し、処理が済み次第自動公開します。

**画像認識によるスコア入力補助**
リザルト画像を Claude API (vision) に渡して SCORE / EX SCORE を抽出し、フォームに自動入力。API キー未設定時や認識失敗時は手入力へフォールバックする安全設計です。

## セキュリティへの取り組み

公開運用を前提に、公開前にはコードベース全体のセルフ監査を実施しました。

- SQL は全箇所パラメータ化 (Dapper)、動的 SQL なし
- サーバー側バリデーション必須 (DataAnnotations + 正規化)。ユーザー入力の DOM 描画は全箇所エスケープ
- 画像アップロードは形式ホワイトリスト + マジックバイト検証 + サイズ上限、ファイル名はサーバー側で GUID 生成
- 管理 API は Basic 認証 (タイミングセーフ比較、未設定時は fail-closed) + レート制限
- Discord Interactions は Ed25519 署名検証
- nginx でレート制限のゾーン分離 (管理 / 公開投稿 / 画像認識)、HSTS・セキュリティヘッダ付与
- 秘密情報は `.env` 管理で Git には含めない

## セットアップ

前提: Docker (Compose v2)

```bash
git clone https://github.com/Kohei-Sugiura/TeamNVN_Web_Project.git
cd TeamNVN_Web_Project
cp .env.example .env   # パスワード等を書き換える
docker compose up -d --build
```

- フロント: `http://localhost:19080/`、ヘルスチェック: `http://localhost:19080/api/health`
- Discord 連携と画像認識は `.env` の該当キー (`DISCORD_*` / `ANTHROPIC_API_KEY`) を設定した場合のみ有効になります。未設定でもサイト本体は動作します
- 本番公開ポートは `WEB_PORT` で変更可。SSL 化は `deploy/setup-ssl.sh` (Let's Encrypt) を使用

## ディレクトリ構成

```
./
├── api/          ASP.NET Core — Controllers / Services / Validation
├── web/          静的フロント — ポータル / WeePA / MSC / 管理画面
├── nginx/        リバースプロキシ設定
├── db/init/      スキーマ定義 SQL (初回起動時に自動適用)
├── deploy/       SSL セットアップ / 更新スクリプト
└── docker-compose.yml
```

## 開発について

- 仕様策定・設計・実装・インフラ構築・本番運用まで個人で担当
- Claude Code を併用した AI 支援開発。仕様書と設計判断を文書化しながら、生成コードは全てレビューして取り込むスタイル
- 運用しながらの段階的拡張 (最小構成の疎通確認 → 投稿/承認フロー → Discord 連携 → 画像認識 → 自動運行) で、本番データを保全しつつ機能追加

### 今後の構想

- 年末の年間集計機能
- 同一人物の X ID 統合機能
- reCAPTCHA v3 の導入
- CSP 導入とフォントのセルフホスト化
