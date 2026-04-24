# CLAUDE.md

Project-level guidance for Claude Code. `claude` を本リポジトリ直下で起動すると自動読み込みされる。
新規タスクに着手する前に、本ファイルと `weepa-spec.md` を必ず読むこと。

## プロジェクト概要

**Team NVN WeePA** — SOUND VOLTEX 交流チーム「Team NVN」のポータルサイトと、
その中の週次スコアアタック企画「WeePA」の管理システム。

- 仕様書（真のソース）: `weepa-spec.md`
- サイトの主役ブランドは **Team NVN**。WeePA は Team NVN の機能の1つとして位置付ける
- トップ (`/index.html`) は Team NVN のポータル。「お知らせ」セクションに現在開催中の WeePA カードが並ぶ構造（将来 Discord 告知・他イベント等も追加予定）

## 技術スタック

| レイヤ | 採用技術 |
|---|---|
| フロント | 素の HTML / CSS / JavaScript（フレームワーク無し） |
| API | ASP.NET Core 8 (.NET 8 LTS) + Dapper 2.1 + Npgsql 8 |
| DB | PostgreSQL 16 |
| プロキシ | Nginx |
| コンテナ | Docker Compose（nginx / api / db の3コンテナ） |

- DB 直アクセスは想定しない（API が唯一の入口）
- ローカル開発: Windows + Docker Desktop（WSL2）
- 本番: Ubuntu 22.04 サーバー（IP 直公開、将来ドメイン + Let's Encrypt 予定）

## 開発ワークフロー

仕様書 `weepa-spec.md` の「実装の段階」（Step 1〜7）に沿って進める。

**1 Step 単位で**:
1. API 実装 → curl で動作確認
2. フロント実装 → ブラウザで動作確認
3. **1 Step = 1 コミット** でまとめて `git push`

**コミットメッセージ形式**: `Step N: <機能名>`（過去例: `Step 5: スコア投稿機能 (画像アップロード付き)`）

## コーディング規約

- コメントは **基本書かない**。WHY が非自明な時だけ簡潔に1行だけ
- 抽象化は必要になってから。現状は Controller が直接 Dapper を叩く最小構成
- 新規ファイル作成より **既存ファイル編集** を優先
- **バリデーションは API 側が必須**（フロントは UX 用の補助扱い）
- X ID 正規化は共通化済み（`api/Validation/XIdNormalizer.cs` を流用）
- submit_id は BEFORE INSERT トリガー自動採番、複合PK衝突時はアプリ側で最大3回リトライ

## 仕様書から外した / 変えた決定事項

| 項目 | 決定 | 理由 |
|---|---|---|
| IP アドレス記録 | **実装しない** | 日本の家庭回線は動的IP中心で実効性低。将来 reCAPTCHA 導入で代替 |
| 楽曲名オートコンプリート (`GET /api/songs/search`) | **実装しない** | 毎週違う曲を提案するので既存曲選択はほぼ無い |
| `pg_trgm` 拡張 / `idx_weepa_songs_name_trgm` | **残す** | 将来の管理者用「曲統合」機能向け |
| 選曲提案は「今週の曲」 | **違う** | 将来の候補プールに積む。UI文言に「今週」「来週」等を入れない |
| ランキング公開 | **集計完了後のみ** | 開催中イベントにはランキングへの導線を置かない。`/ranking.html` は存在するが直URL経由のみ |
| 集計完了の判定方法 | **未定** | Step 6 or 後工程で決定（候補: `end_date < NOW()` で自動 / 管理者が `tallied_at` を明示） |
| `approval_status` の値設計 | **負値=終端（却下）／非負=前進パイプライン** | `-1=却下 / 0=未承認 / 1=承認済み`。「非却下」は `approval_status >= 0` で綺麗に書ける。songs/scores 両テーブル共通 |
| 楽曲の「実施済み」状態 | **`approval_status=2` を実施済みとして追加** | イベント作成時（手動・auto-weekly 双方）で対象曲を 1→2 に遷移。auto-weekly の抽選対象は status=1 のみなので実施済みは再度選ばれない。スコア側には実施済み相当の状態は無い |

## UI/UX ルール

### 1. 管理者の裏処理を一般ユーザー向けUIに露出しない

- NG: 「管理者の承認後に掲載されます」「審査中」「承認をお待ちください」
- OK: 「投稿を受け付けました」「投稿が完了しました」
- 管理者画面 (`/admin.html` 等) 内では「承認」「却下」等の用語OK

### 2. 全ページ モバイル対応必須

- `<meta name="viewport" content="width=device-width, initial-scale=1.0">` 必須
- `@media (max-width: 600px)` ブロックで小画面最適化
- 入力 font-size は **1rem 以上**（iOS 自動ズーム対策）
- テーブルは `overflow-x: auto` + `min-width` で横スクロール許容
- 送信ボタンは mobile で `width: 100%`
- タップ領域は最低 44px 角相当

### 3. ビジュアル: SOUND VOLTEX 風ネオン×ダーク

- 背景: 濃紺〜黒の radial gradient + 斜めスキャンライン overlay（`body` に設定済み）
- プライマリ: シアン `#00e5ff`（フォーカス・リンク・境界・ラベル強調）
- アクセント: ピンク `#ff3d8a`、紫 `#bb00ff`（ボタン・バッジ・必須マーク・エラー）
- パネルは半透明 `rgba(20, 20, 42, 0.65)` + `backdrop-filter: blur(10px)`
- H1 は gradient text（`background-clip: text`）、ボタンは3色グラデ + `box-shadow` グロー
- 過度なアニメーションは入れない（可読性・パフォーマンス優先）
- 既存のカード/パネル/ボタン/テーブルのスタイルを **必ず流用**（独自色を新規に足さない）
- スタイル詳細は `web/css/style.css`

## コマンド提示時のフォーマット規約

ユーザーに実行してもらうコマンドは **常に 1 行で提示** する。

- `\` による行継続禁止
- `-d '{...}'` の JSON は minified（改行・インデントなし）
- コメント行（`#`）は別行でOK
- コピペで即実行できる形にする

## 現在の進捗

| Step | 内容 | 状態 |
|---|---|---|
| 1 | 最小構成の3コンテナ疎通 (nginx/api/db) | ✅ |
| 2 | DBスキーマ投入 + API から DB 接続 (Dapper + Npgsql) | ✅ |
| 3 | 選曲提案機能（POST /api/songs + `/submit.html`） | ✅ |
| 4 | イベント機能 + ランキング API + Team NVN トップ刷新 + SDVXテーマ | ✅ |
| 5 | スコア投稿機能（multipart 画像アップロード付き） | ✅ |
| 6 | **管理者画面と承認フロー** | ⏳ **次** |
| 7 | デプロイ（Ubuntu サーバー公開は完了、SSL化・デプロイスクリプト整備は残り） | 🔶 部分完了 |

## Step 6 のスコープ（仕様書準拠 + 将来を見据えた調整）

仕様書の Step 6 は Web 管理画面ベース。**まず `admin.html` だけで承認運用が完結すること** を最優先で仕上げる。
Discord Bot 連携は将来計画として残すが、HTML 側の運用が安定稼働するのを待って着手する（2026-04-24 決定）。
ただし API 設計は Discord Bot からも後から叩ける純粋な REST のまま維持し、HTML 専用の仕様は混ぜない。

- 管理者認証: Basic 認証 or Cookie（どちらか相談）
- エンドポイント:
  - `GET  /api/admin/songs/pending` 未承認の選曲提案一覧
  - `POST /api/admin/songs/{id}/approve` / `reject`
  - `GET  /api/admin/scores/pending` 未承認スコア一覧
  - `POST /api/admin/scores/{keys}/approve` （**body 任意で `score`/`exScore` を受理 → 渡されれば修正値で承認**）
  - `POST /api/admin/scores/{keys}/reject` （body 不要、単純に却下）
- 管理者のスコア修正フロー: 画像と投稿値がズレていれば **「修正して承認」で修正値を渡す** → 承認扱いでランキング対象
- 承認時: pending 画像を物理削除
- 却下時: 画像を一定期間保持（将来のバッチ削除）
- `admin.html`: 未承認リスト + 承認/却下ボタン + 却下時のスコア修正UI。実運用できる品質に仕上げる

## 将来計画（Step 8 以降の構想）

### Discord Bot 連携（優先度高）

1. ユーザーがスコア投稿 → pending 状態で保存（実装済み）
2. **Discord Bot が管理者用Discord チャンネルに画像+スコア+コマンドボタンを投稿**（未実装）
3. 管理者が Discord 上で OK / NG（スコア修正含む）を操作
4. Bot が API (`/api/admin/scores/.../approve|reject`) を叩いて状態更新
5. 承認 → 画像物理削除

Bot は本リポジトリとは別プロジェクトになる可能性あり。連携APIは `Step 6` で整える。

### その他（仕様書記載 or 仕様書補足）

- 独自ドメイン + Let's Encrypt SSL 化
- 3曲通しスコアタ対応（別テーブル `weepa_course_*` を想定）
- 年末集計機能
- X ID 統合機能（管理者画面で別名同一人物を統合）
- OCR でスコア自動入力
- reCAPTCHA v3

## サーバー環境（本番）

- Ubuntu 22.04.5 LTS / x86_64
- Docker + Docker Compose 済み
- `.env` はサーバー上 `~/weepa/.env`（git管理外、権限 600）
- 画像 uploads は Docker 名前付きボリューム `uploads` に保存（`/app/uploads/pending/{event_id}/...`）
- リポジトリクローン先: `~/weepa`
- 80 番公開中（IP 直打ち段階）

## ディレクトリ

```
./
├── api/                      ASP.NET Core (.NET 8)
│   ├── Controllers/          Health / Songs / Events / Scores / Admin
│   ├── Models/
│   │   ├── Requests/         入力DTO（DataAnnotations付き）
│   │   └── Responses/        応答DTO
│   ├── Validation/           XIdNormalizer
│   ├── Program.cs            NpgsqlDataSource をDI
│   ├── Weepa.Api.csproj
│   └── Dockerfile
├── web/                      静的フロント
│   ├── index.html            Team NVN ポータル
│   ├── submit.html           楽曲投稿フォーム
│   ├── score.html            スコア投稿フォーム
│   ├── ranking.html          ランキング（直URLのみ）
│   ├── css/style.css         SDVX風ネオンテーマ + モバイル対応
│   └── js/                   main / submit / score / ranking
├── nginx/conf.d/default.conf
├── db/init/01_schema.sql     初回起動時のみ実行される
├── deploy/                   将来のデプロイスクリプト置き場（空）
├── docker-compose.yml
├── .env.example
├── weepa-spec.md             仕様書（真のソース）
└── CLAUDE.md                 ← 本ファイル
```

## Claude Code セッション向け Tips

- ユーザー指示で **仕様書と異なる判断** が出たら、CLAUDE.md の該当表に追記し、`memory/` にも `project_*.md` として保存する
- ユーザーの **作業スタイルの好み**（コマンド形式・UI方針など）が出たら `memory/feedback_*.md` に保存
- 新規ページ / 新規スタイルは必ず既存の SDVX テーマに沿わせる。独自配色・独自フォント・独自アニメーションは入れない
- 大きな画像追加・外部フォント導入・CDN 依存 は **必ずユーザー確認してから**
