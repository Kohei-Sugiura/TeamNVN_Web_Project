# Team NVN Web Project

SOUND VOLTEX 交流チーム「Team NVN」のポータルサイトと、イベント管理システム。

週次スコアアタック企画 **WeePA** を中心に、選曲提案 → 承認 → 開催 → スコア投稿(画像付き) → 承認 → ランキング公開までの運用を Web + Discord で完結させる。

- 本番: https://team-nvn-server.com/ (Ubuntu 22.04 / Docker Compose / Let's Encrypt)
- 開発スタイル: 仕様策定から実装・運用まで個人開発。Claude Code を併用した AI 支援開発

## 主な機能

| 機能 | 概要 |
|---|---|
| 選曲提案 | 課題曲をフォームから提案。管理者承認で候補プール入り |
| イベント自動作成 | 毎週の開催枠をスケジューラが自動生成(曜日・時刻・期間は DB 設定で変更可) |
| スコア投稿 | リザルト画像必須の multipart 投稿。Claude API による画像からのスコア自動読取 (OCR) 対応 |
| 参加部門 (Tier) | 1=ストイック(EXスコア) / 2=チャレンジ(スコア) / 3=カジュアル(提出順+自己ベスト更新幅) の3部門制 |
| 承認フロー | Web 管理画面 + Discord 通知のボタン操作(承認 / 修正承認 / 却下)の二系統 |
| 結果自動発表 | イベント終了を毎分 tick が検知し、Tier 別結果画像を Discord へ自動投稿 (puppeteer でランキングを PNG 化) |
| ランキング自動公開 | 終了 + 設定時間(デフォルト24h)で自動公開。未処理投稿があれば保留し、処理後に公開 |
| MSC イベント | 複数曲・通しプレー対応のイベント形式 (arcade / konaste 両対応、曲別画像つき投稿) |
| オンマチ | Discord 上のオンラインマッチング部屋管理 Bot (統合ボードから入退室、部屋自動生成) |
| 管理画面 | 承認キュー処理・イベント管理・公開設定・スケジュール設定・DB メンテナンス UI (Basic 認証) |

## 技術スタック

| レイヤ | 採用技術 |
|---|---|
| フロント | 素の HTML / CSS / JavaScript (フレームワーク無し) |
| API | ASP.NET Core 8 (.NET 8 LTS) + Dapper 2.1 + Npgsql 8 |
| DB | PostgreSQL 16 |
| プロキシ | Nginx (SSL 終端 / レート制限 / セキュリティヘッダ) |
| コンテナ | Docker Compose (nginx / api / db / scheduler) |
| 外部連携 | Discord (Interactions + Bot API)、Anthropic Claude API (画像認識) |

## アーキテクチャ

```
[ブラウザ]
    ↓ HTTPS (Let's Encrypt)
[nginx コンテナ]
  - リバースプロキシ / SSL 終端
  - 静的ファイル配信 (HTML/CSS/JS)
  - 投稿画像の配信
  - レート制限 (admin 30r/m、公開 POST 10r/m、OCR 6r/m)
    ↓
[api コンテナ]  ASP.NET Core
  - REST API (投稿 / 承認 / ランキング / 管理)
  - 画像アップロード処理 (マジックバイト検証付き)
  - Discord Interactions (Ed25519 署名検証)
  - 毎分 tick (イベント終了検知 → 結果発表 / 自動公開)
    ↓
[db コンテナ]  PostgreSQL 16 (Volume 永続化)

[scheduler コンテナ]  cron で週次イベント自動作成 API を定期実行
```

- ブラウザからは nginx のみアクセス可能。api / db は Docker 内部ネットワークのみで外部非公開
- DB 直アクセスは想定しない (API が唯一の入口)
- `/` → 静的配信、`/api/*` → api へプロキシ、`/uploads/*` → 投稿画像配信

## ディレクトリ

```
./
├── api/                      ASP.NET Core (.NET 8)
│   ├── Controllers/          Health / Songs / Events / Scores / Admin / Msc / Discord ほか
│   ├── Models/
│   │   ├── Requests/         入力DTO (DataAnnotations 付き)
│   │   └── Responses/        応答DTO
│   ├── Services/             Discord 通知 / コマンド同期 / 画像認識 / スクリーンショット / tick
│   ├── Validation/           XIdNormalizer / AdminBasicAuth / ImageSignature
│   ├── Program.cs            NpgsqlDataSource を DI、レート制限
│   └── Dockerfile
├── web/                      静的フロント (URL は拡張子レス、各ディレクトリの index.html)
│   ├── index.html            Team NVN ポータル ( / )
│   ├── weepa/                WeePA 公開ページ (submit / score / ranking / history)
│   ├── msc/                  MSC 公開ページ
│   ├── admin/                管理画面 (Basic 認証)
│   ├── css/style.css         SDVX 風ネオンテーマ + モバイル対応
│   └── js/
├── nginx/conf.d/default.conf
├── db/init/                  初回起動時に実行されるスキーマ SQL
├── deploy/                   SSL セットアップ / 更新スクリプト
├── docker-compose.yml
└── .env.example
```

## セットアップ (ローカル開発)

前提: Docker Desktop

```bash
# 1. .env を用意 (パスワード等を書き換える)
cp .env.example .env

# 2. 起動
docker compose up -d --build

# 3. 動作確認 (開発時はデフォルトで WEB_PORT=19080)
#    フロント:        http://localhost:19080/
#    ヘルスチェック:  http://localhost:19080/api/health

# 4. 停止
docker compose down
```

- 本番 Linux で 80/443 を使う場合は `.env` で `WEB_PORT=80` に変更 (Windows は IIS が 80 を握るため開発既定は 19080)
- Discord 連携 / 画像認識は `.env` の該当キー (`DISCORD_*` / `ANTHROPIC_API_KEY`) を設定した時のみ有効。未設定でもサイトは通常動作

## デプロイ (本番)

```
[ローカル開発] → git push → [GitHub] → サーバーで git pull → docker compose up -d --build
```

### SSL 化 (Let's Encrypt)

1. ドメインの A レコードをサーバー公開 IP に向け、80/443 を開放
2. `.env` に `DOMAIN_NAME` / `LETSENCRYPT_EMAIL` を設定し、`WEB_PORT=80` に切り替え
3. 初回発行:

   ```bash
   docker compose up -d
   ./deploy/setup-ssl.sh
   ```

   スクリプトは ①ACME challenge の到達性を事前確認 → ②certbot (webroot) で発行 → ③SSL テンプレートを実ドメインで埋めて `default.conf` を上書き (元ファイルは `.bak.<timestamp>` に退避) → ④`nginx -t` 検証後 reload を行う

4. 自動更新 (毎日 cron。certbot は 30 日以内に切れる証明書のみ更新する冪等な作り):

   ```
   30 3 * * * /path/to/repo/deploy/renew-ssl.sh >> /var/log/letsencrypt-renew.log 2>&1
   ```

HTTP 構成に戻す場合は退避した `.bak.<timestamp>` を `default.conf` に戻して `nginx -s reload`。

### 週次イベント自動作成

- API: `POST /api/admin/events/auto-weekly` (Basic 認証)
- 動作: 承認済み・未実施の楽曲からランダムで 1 曲選び、次回開催枠を作成。重複期間があれば `409 Conflict`
- 開催曜日・時刻・期間は DB の `weepa_schedule_config` が決定 (管理画面 or `PUT /api/admin/schedule/config` で変更)
- `scheduler` コンテナが cron (`AUTO_WEEKLY_TRIGGER_CRON`) で定期的にこの API を叩く。cron は「いつチェックするか」だけを決め、実際の開催日時は DB 設定が決める

## DB 設計

スキーマの真実は `db/init/*.sql`。中核は WeePA の 3 テーブル:

```
weepa_songs (選曲提案 + 楽曲マスタ兼用)
    ↓ 1:N
weepa_events (開催イベント)
    ↓ 1:N
weepa_scores (スコア投稿、複合PK = event_id + x_id + submit_id)
```

設計上のポイント:

- **approval_status は負値=終端 / 非負=前進パイプライン** (`-1`=却下 / `0`=未承認 / `1`=承認済み)。「非却下」を `approval_status >= 0` で書ける。songs / scores 共通
- **submit_id は BEFORE INSERT トリガーで自動採番** (同一人物の同イベント投稿回数 1,2,3...)。複合 PK 衝突時はアプリ側で最大 3 回リトライ。スコアは全履歴を残す
- **楽曲の「実施済み」判定は events への紐付きで派生** (専用ステータス列を持たない)。自動選曲は `status=1 AND NOT EXISTS event` から選出
- **Tier (参加部門)**: 所属部門は最後の投稿の tier、スコア集計は部門横断で score / ex_score それぞれ MAX
- ランキング・承認キュー用の部分インデックス、楽曲名の trigram インデックス (`pg_trgm`、将来の曲統合機能向け) を整備
- ユーザー識別は X (Twitter) ID。入力は正規化して保存: `@abc` / `https://x.com/abc` → `abc`、全角 @ 変換、小文字化、`^[A-Za-z0-9_]{1,15}$` 検証

## API 設計

REST のみ (HTML 専用仕様は混ぜない)。Discord Bot も同じ管理 API を Basic 認証の loopback で叩く。

### 一般ユーザー向け (認証なし)

| メソッド | パス | 機能 |
|---|---|---|
| GET | `/api/health` | ヘルスチェック |
| POST | `/api/songs` | 選曲提案の投稿 |
| GET | `/api/songs` | 提案一覧 (開催履歴ページ用) |
| GET | `/api/events/current` | 現在開催中のイベント情報 |
| GET | `/api/events/{id}` | イベント詳細 |
| GET | `/api/events/{id}/ranking` | ランキング (公開済みのみ) |
| POST | `/api/events/{id}/scores` | スコア投稿 (multipart 画像付き) |
| POST | `/api/score/recognize` | リザルト画像から SCORE/EX を自動読取 (レート制限付き) |
| GET/POST | `/api/msc/...` | MSC イベントの参照・投稿 |

### 管理者向け (Basic 認証)

| メソッド | パス | 機能 |
|---|---|---|
| GET | `/api/admin/songs/pending` | 未承認の選曲提案一覧 |
| POST | `/api/admin/songs/{id}/approve` / `reject` | 選曲の承認 / 却下 |
| GET | `/api/admin/scores/pending` | 未承認スコア一覧 |
| POST | `/api/admin/scores/{keys}/approve` | スコア承認 (body 任意で `score`/`exScore` → 修正値で承認) |
| POST | `/api/admin/scores/{keys}/reject` | スコア却下 |
| POST | `/api/admin/events` / `auto-weekly` | イベント作成 / 週次自動作成 |
| PUT | `/api/admin/schedule/config` | 開催スケジュール設定 (未来イベントの再計算付き) |
| ほか | `/api/admin/db/...` | DB メンテナンス UI 用 (テーブル別の固定 SQL エンドポイント) |

### バリデーション方針

- **サーバー側が必須** (セキュリティ・整合性)。フロントは UX 用の補助
- DataAnnotations (`[Required]` / `[StringLength]` / `[RegularExpression]` / `[Range]`) + `[ApiController]` の自動 400
- 画像は Content-Type ホワイトリスト (JPEG/PNG/WebP) + マジックバイト検証 + サイズ上限。保存ファイル名はサーバー側 GUID 生成

### 運用フロー (投稿 → 公開)

1. ユーザーが選曲提案 → 管理者承認で候補プール入り
2. スケジューラが週次で候補から自動選曲しイベント作成
3. 参加者がスコア投稿 (画像必須) → Discord へ通知、ボタンで承認 / 修正承認 / 却下
4. 承認時は画像を物理削除 (容量節約)、却下時は一定期間保持
5. イベント終了を tick が検知 → Tier 別結果を Discord に自動発表
6. 終了 + 24h (設定可) でランキング自動公開。未処理投稿があれば処理完了まで保留

## DB 運用早見表 (ad-hoc SQL)

> [!CAUTION]
> 修正系は必ずトランザクションで囲い、結果を確認してから COMMIT する。

```bash
# psql 起動
docker compose exec db psql -U $POSTGRES_USER -d $POSTGRES_DB
```

便利なメタコマンド: `\dt` (テーブル一覧) / `\d <table>` (定義) / `\timing on` / `\x` (縦表示) / `\q`

```sql
-- 現在開催中のイベント
SELECT e.weepa_event_id, s.song, s.effect, e.start_date, e.end_date
FROM weepa_events e JOIN weepa_songs s ON s.weepa_song_id = e.weepa_song_id
WHERE NOW() BETWEEN e.start_date AND e.end_date;

-- 承認待ち (楽曲 / スコア)
SELECT * FROM weepa_songs  WHERE approval_status = 0 ORDER BY submit_date;
SELECT * FROM weepa_scores WHERE approval_status = 0 ORDER BY submit_date;

-- イベントを公開 / 非公開
UPDATE weepa_events SET tallied_at = NOW() WHERE weepa_event_id = 12;  -- 公開
UPDATE weepa_events SET tallied_at = NULL  WHERE weepa_event_id = 12;  -- 非公開

-- 過去開催を一括インポート (song + event を同一 TX、過去データは最初から承認済みで)
BEGIN;
WITH s AS (
  INSERT INTO weepa_songs (song, effect, x_id, submit_name, approval_status, submit_date)
  VALUES ('Song Title', 'MXM', 'example_user', '表示名', 1, '2026-04-10 12:00:00+09')
  RETURNING weepa_song_id
)
INSERT INTO weepa_events (weepa_song_id, start_date, end_date, tallied_at)
SELECT weepa_song_id, '2026-04-15 21:00:00+09', '2026-04-22 18:00:00+09', NOW() FROM s;
COMMIT;

-- 過去スコアのインポート (submit_id はトリガー採番、画像なしは placeholder)
INSERT INTO weepa_scores (weepa_event_id, x_id, submit_name, score, ex_score, image_path, approval_status)
VALUES (1, 'example_user', '表示名', 9953925, 5325, '/uploads/legacy/none.png', 1);
```

注意点:

- SQL で楽曲を承認してもイベントは自動生成されない (API 経由と違う)。スケジュール設定を SQL 直で変えると未来イベントの再計算も走らない
- 試験データ削除は子 (scores) → 親 (events) の順でトランザクション内で

```bash
# バックアップ / リストア (リストアは空 DB に対して)
docker compose exec -T db pg_dump -U $POSTGRES_USER $POSTGRES_DB > backup-$(date +%Y%m%d-%H%M).sql
docker compose exec -T db psql -U $POSTGRES_USER -d $POSTGRES_DB < backup-20260430-2100.sql

# 却下画像のお片付け (90日経過分。実行前に -print で確認)
docker compose exec api find /app/uploads/pending -type f -mtime +90 -print
docker compose exec api find /app/uploads/pending -type f -mtime +90 -delete
```

## 開発ポリシー

### コーディング規約

- コメントは基本書かない。WHY が非自明な時だけ簡潔に 1 行
- 抽象化は必要になってから。Controller が直接 Dapper を叩く最小構成
- 新規ファイル作成より既存ファイル編集を優先
- 1 機能 = 1 コミットでまとめて push (例: `feat: イベント終了時の結果自動発表`)

### UI/UX ルール

- **管理者の裏処理を一般ユーザー向け UI に露出しない** (「承認待ち」「審査中」は NG、「投稿を受け付けました」と言う)
- **全ページ モバイル対応必須**: viewport メタ、`@media (max-width: 600px)`、入力 font-size 1rem 以上 (iOS ズーム対策)、テーブルは横スクロール許容、タップ領域 44px 角
- **ビジュアルは SOUND VOLTEX 風ネオン×ダーク**: 濃紺〜黒 radial gradient + スキャンライン、シアン `#00e5ff` / ピンク `#ff3d8a` / 紫 `#bb00ff`、半透明パネル + blur。新規ページも既存テーマのスタイルを流用し独自色を足さない。過度なアニメーションは入れない

### 主な設計判断の記録

| 項目 | 決定 | 理由 |
|---|---|---|
| IP アドレス記録 | 実装しない | 日本の家庭回線は動的 IP 中心で実効性が低い。将来 reCAPTCHA で代替 |
| 楽曲オートコンプリート | 実装しない | 毎週違う曲を提案する運用で既存曲選択がほぼ発生しない |
| `pg_trgm` インデックス | 残す | 将来の管理者用「曲統合」機能向け |
| ランキング公開 | 集計完了後のみ | 開催中イベントにランキング導線を置かない (直 URL のみ) |
| `approval_status` | 負=終端 / 非負=前進 | 「非却下」が `>= 0` で綺麗に書ける |
| Tier 制 | 3 部門 (EX / スコア / カジュアル) | 参加層拡大施策。所属は最後の投稿、集計は部門横断 MAX |
| 実施済み判定 | events への紐付きで派生 | 専用ステータス列を持たず整合性を保つ |
| Discord スラッシュコマンド | 大半を廃止しボタン操作へ | 承認はボタン + 通知で完結。結果発表・公開も自動化 |
| 結果発表 / 公開 | tick による全自動 | `end_date` 経過検知 → 発表、+24h で自動公開。未処理投稿があれば保留 |

### スパム・不正対策

- nginx レート制限 (管理 API / 公開 POST / OCR をゾーン分離) + `client_max_body_size`
- 画像はマジックバイト検証・サーバー生成ファイル名・形式/サイズ制限
- Discord Interactions は Ed25519 署名検証、管理 API は fail-closed な Basic 認証 (タイミングセーフ比較)
- スコア投稿はリザルト画像必須 + 管理者の目視承認が最終防衛線
- reCAPTCHA v3 は将来導入予定

## 実装の歩み

Google フォーム運用だった選曲提案・スコア集計の Web 化から始まり、段階的に拡張:

| Step | 内容 | 状態 |
|---|---|---|
| 1 | 最小構成の 3 コンテナ疎通 (nginx/api/db) | ✅ |
| 2 | DB スキーマ投入 + Dapper/Npgsql 接続 | ✅ |
| 3 | 選曲提案機能 | ✅ |
| 4 | イベント機能 + ランキング + SDVX テーマ | ✅ |
| 5 | スコア投稿 (画像アップロード付き) | ✅ |
| 6 | 管理画面と承認フロー | ✅ |
| 7 | 本番公開 + SSL 化 | ✅ |
| 8+ | Discord 連携 / OCR / Tier 制 / MSC / オンマチ / 自動発表・自動公開 | ✅ 順次拡張中 |

### 今後の構想

- 年末集計機能
- X ID 統合機能 (別名同一人物の統合)
- 3 曲通しスコアタの拡張
- reCAPTCHA v3

## 用語集

| 用語 | 意味 |
|---|---|
| スコアタ | スコアアタックの略 |
| WeePA | 週次スコアアタック企画の名称 |
| エフェクト | SDVX の譜面種別 (NOV / ADV / EXH / MXM / INF / GRV / HVN / VVD / XCD / ULT) |
| X ID | X (Twitter) のユーザー名から `@` を除いた部分。ユーザー識別子 |
| 開催 (イベント) | 採用された課題曲でスコアタを実施する期間 |
| 承認フロー | 投稿された選曲・スコアを管理者がチェックして公開する流れ |
| オンマチ | オンラインマッチング。Discord 上の対戦部屋運用 |
