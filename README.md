# Team NVN Web Project

SOUND VOLTEX 交流チーム「Team NVN」のポータルサイト / イベント運営システム。

チーム内で毎週開催しているスコアアタック企画 **WeePA** を軸に、選曲の募集 → 開催 → スコア集計 → 結果発表までの運営を Web + Discord で自動化している。

🔗 https://team-nvn-server.com/

## 機能

### 参加者向け

- **選曲提案** — 次回の課題曲をフォームから提案
- **スコア投稿** — リザルト画像を添えてスコアを投稿。画像から SCORE / EX SCORE を自動読取 (Claude API による画像認識、失敗時は手入力にフォールバック)
- **3つの参加部門 (Tier)** — EX スコア部門 / スコア部門 / 自己ベスト更新を競うカジュアル部門
- **ランキング / 開催履歴の閲覧**

### 運営向け

- **承認フロー** — 投稿された選曲・スコアを Web 管理画面または Discord 通知のボタン (承認 / 修正承認 / 却下) で処理
- **イベントの自動運行** — 週次の開催枠を自動作成。終了を検知して Tier 別の結果画像を Discord に自動投稿し、一定時間後にランキングを自動公開
- **オンマチ (オンラインマッチング)** — Discord 上の対戦部屋を統合ボードから自動生成・入退室管理する Bot
- **MSC イベント** — 複数曲・通しプレー形式のイベント (アーケード / コナステ両対応)

## 技術スタック

| レイヤ | 採用技術 |
|---|---|
| フロントエンド | HTML / CSS / JavaScript (フレームワーク不使用) |
| バックエンド | C# / ASP.NET Core 8 + Dapper + Npgsql |
| データベース | PostgreSQL 16 |
| Web サーバー | Nginx |
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
- Discord Bot も専用経路を持たず、同じ管理 API を Basic 認証で叩く

## 設計上のポイント

- **スコアは全履歴を保持** — 「イベント × ユーザー × 投稿回数」の複合主キー。投稿回数はトリガーで自動採番し、同時投稿の衝突はアプリ側リトライで解決。カジュアル部門の「自己ベスト更新幅」は履歴から導出
- **状態は派生させる** — 承認ステータスは「負=終端 / 非負=前進」の規約で「非却下」を `>= 0` の一条件で表現。楽曲の「実施済み」も専用フラグを持たず開催レコードへの紐付きから導出
- **イベント運行の自動化** — API 内の毎分 tick がイベント終了を検知し、ランキングページを headless Chromium で画像化して Discord に自動発表。未処理の投稿が残っている場合は公開を保留
- **入力は信用しない** — SQL は全箇所パラメータ化、バリデーションはサーバー側必須、画像はマジックバイト検証 + サーバー側 GUID ファイル名、Discord Interactions は Ed25519 署名検証、管理 API は Basic 認証 + レート制限

## セットアップ

前提: Docker (Compose v2)

```bash
git clone https://github.com/Kohei-Sugiura/TeamNVN_Web_Project.git
cd TeamNVN_Web_Project
cp .env.example .env   # パスワード等を書き換える
docker compose up -d --build
```

- フロント: `http://localhost:19080/`、ヘルスチェック: `http://localhost:19080/api/health`
- Discord 連携と画像認識は `.env` の該当キー (`DISCORD_*` / `ANTHROPIC_API_KEY`) を設定した場合のみ有効。未設定でもサイト本体は動作する
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

## 今後の構想

- 年末の年間集計機能
- 同一人物の X ID 統合機能
- reCAPTCHA v3 の導入
- CSP 導入とフォントのセルフホスト化
