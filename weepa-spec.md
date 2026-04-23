# Weepa スコアタ管理サイト 実装仕様書

## プロジェクト概要

音ゲー（SOUND VOLTEX）の交流チームで週1で開催しているスコアアタック（スコアタ）の管理サイト。
現在Googleフォームで運用している選曲提案とスコア投稿をWeb化し、集計も自動化する。

## 技術スタック

- **フロントエンド**: HTML / CSS / JavaScript（素の構成、フレームワークなし）
- **バックエンドAPI**: C# / ASP.NET Core (.NET 8 LTS)
- **データベース**: PostgreSQL
- **リバースプロキシ**: Nginx
- **コンテナ**: Docker / Docker Compose（3コンテナ構成）
- **開発環境**: ローカル Windows + Docker Desktop
- **本番環境**: Linux (Ubuntu) サーバー

## アーキテクチャ

### コンテナ構成（3つに分離）

```
[ブラウザ]
    ↓ HTTPS (将来) / HTTP (初期)
[nginx コンテナ]
  - リバースプロキシ
  - 静的ファイル配信 (HTML/CSS/JS)
  - 承認待ち画像の配信
  - SSL終端（将来、Let's Encrypt）
    ↓
[api コンテナ]
  - ASP.NET Core (.NET 8)
  - 画像アップロード処理
    ↓
[db コンテナ]
  - PostgreSQL
  - Volumeでデータ永続化
```

### 通信フロー

- ブラウザからは `nginx` のみアクセス可能
- `api` と `db` は Docker の内部ネットワークで通信、外部非公開
- `/` → nginx が静的ファイル配信
- `/api/*` → nginx が api コンテナへプロキシ
- `/uploads/*` → nginx が承認待ち画像を配信（api コンテナが書き込むボリューム）

## 運用ポリシー

### 認証

- **投稿側（一般ユーザー）**: 認証なし
- **管理者ページ**: Basic認証 or Cookie認証（シンプル実装で）
- 管理者は少人数

### ユーザー識別

- 投稿者は **X（Twitter）のID** と **表示名** を入力
- 同一人物の識別は **X ID** で行う
- X ID変更時は管理者画面で統合（将来機能）

### スパム・不正対策

- reCAPTCHA v3（将来）
- IPアドレスを投稿レコードに記録
- Nginxで client_max_body_size 制限
- API側でバリデーション（長さ、形式、範囲）
- スコア投稿には **リザルト画像アップロード必須**
  - 投稿時: 承認待ち状態で画像保持
  - 管理者承認時: 画像を物理削除（容量節約）
  - 却下時: 画像は一定期間保持して削除

### 選曲・開催フロー

1. ユーザーが選曲提案フォームに投稿（曲名・譜面種・選曲理由など）
2. 管理者が承認
3. 週1で開催（採用された提案から）
4. 参加者がスコア投稿（画像付き）
5. 管理者が承認 → 画像削除
6. ランキング表示

### 楽曲マスタ管理

- **手動登録運用**（外部データソースは使わない）
- 選曲提案フォームで **オートコンプリート**（既存曲の表記ゆれ防止）
- 既存曲なら候補から選択、新規なら新規登録

## データベース設計

### ER図

```
weepa_songs (楽曲投稿=選曲提案+楽曲マスタの兼用)
    ↓
weepa_events (開催イベント)
    ↓
weepa_scores (スコア投稿)
```

**注**: 今回の議論では楽曲マスタと選曲提案を1テーブル（weepa_songs）に統合する方針で決定。
同じ曲の別提案は別レコードとなる（表記ゆれ対策はAPI側でのオートコンプリートと管理者統合機能で対応）。

### テーブル定義

```sql
-- ============================================
-- weepa_songs（楽曲投稿＝選曲提案）
-- ユーザーからの選曲提案を保存する
-- ============================================
CREATE TABLE weepa_songs (
    weepa_song_id   SERIAL PRIMARY KEY,
    song            TEXT NOT NULL,                          -- 楽曲名
    effect          TEXT NOT NULL,                          -- 譜面種 (NOV/ADV/EXH/MXM/INF/GRV/HVN/VVD/XCD/ULT)
    x_id            TEXT NOT NULL,                          -- 提案者のXのID（@なし）
    submit_name     TEXT NOT NULL,                          -- 選曲者の表示名
    submit_reason   TEXT,                                   -- 選曲理由
    submit_date     TIMESTAMPTZ NOT NULL DEFAULT NOW(),     -- 投稿日時
    approval_status SMALLINT NOT NULL DEFAULT 0             -- 0:未承認 / 1:承認 / 2:却下
);

-- ============================================
-- weepa_events（週1のスコアタ開催イベント）
-- 承認済み楽曲から開催を作成
-- ============================================
CREATE TABLE weepa_events (
    weepa_event_id  SERIAL PRIMARY KEY,
    weepa_song_id   INTEGER NOT NULL REFERENCES weepa_songs(weepa_song_id),
    start_date      TIMESTAMPTZ NOT NULL,                   -- 開催期間_開始日
    end_date        TIMESTAMPTZ NOT NULL,                   -- 開催期間_終了日
    
    CONSTRAINT valid_date_range CHECK (end_date > start_date)
);

-- ============================================
-- weepa_scores（スコア投稿）
-- 同じ人が同じイベントで複数回スコア更新可能（履歴を全部残す）
-- ============================================
CREATE TABLE weepa_scores (
    weepa_event_id  INTEGER NOT NULL REFERENCES weepa_events(weepa_event_id),
    x_id            TEXT NOT NULL,                          -- XのID
    submit_id       INTEGER NOT NULL,                       -- 同じ人の同イベント投稿回数(1,2,3...)
    submit_name     TEXT NOT NULL,                          -- 表示名
    score           INTEGER NOT NULL CHECK (score BETWEEN 0 AND 10000000),
    ex_score        INTEGER,                                -- EXスコア（任意）
    submit_date     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    approval_status SMALLINT NOT NULL DEFAULT 0,            -- 0:未承認 / 1:承認 / 2:却下
    
    PRIMARY KEY (weepa_event_id, x_id, submit_id)
);
```

### インデックス

```sql
-- 未承認投稿の検索（管理者画面用）
CREATE INDEX idx_weepa_songs_status 
    ON weepa_songs (approval_status, submit_date);

CREATE INDEX idx_weepa_scores_status 
    ON weepa_scores (approval_status, submit_date);

-- ランキング表示（承認済みのみ、スコア順）
CREATE INDEX idx_weepa_scores_ranking 
    ON weepa_scores (weepa_event_id, score DESC) 
    WHERE approval_status = 1;

-- 開催中のイベント検索
CREATE INDEX idx_weepa_events_period 
    ON weepa_events (start_date, end_date);

-- 楽曲名の部分一致検索（オートコンプリート用）
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX idx_weepa_songs_name_trgm 
    ON weepa_songs USING gin (song gin_trgm_ops);
```

### submit_id の自動採番

`weepa_scores` の `submit_id` は **BEFORE INSERT トリガー** で自動採番。
アプリ側からはsubmit_idを指定せずINSERTできるようにする。

```sql
CREATE OR REPLACE FUNCTION weepa_scores_assign_submit_id()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.submit_id IS NULL THEN
        SELECT COALESCE(MAX(submit_id), 0) + 1
        INTO NEW.submit_id
        FROM weepa_scores
        WHERE weepa_event_id = NEW.weepa_event_id
          AND x_id = NEW.x_id;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_weepa_scores_submit_id
BEFORE INSERT ON weepa_scores
FOR EACH ROW
EXECUTE FUNCTION weepa_scores_assign_submit_id();
```

複合PK違反による同時実行衝突は**アプリ側で1〜2回リトライ**で対応。

## 3曲通しスコアタ（将来拡張）

将来的に3曲通しスコアタも対応予定。その際は**別テーブル**で管理する方針。
現時点では単曲スコアタのみ実装。

```
weepa_course_events (将来追加予定)
weepa_course_charts
weepa_course_scores
```

## API設計（基本）

### エンドポイント一覧（初期実装分）

#### 一般ユーザー向け

| メソッド | パス | 機能 |
|---|---|---|
| GET | `/api/health` | ヘルスチェック |
| GET | `/api/songs/search?q=xxx` | 楽曲名オートコンプリート |
| POST | `/api/songs` | 選曲提案の投稿 |
| GET | `/api/events/current` | 現在開催中のイベント情報 |
| GET | `/api/events/{id}` | 指定イベントの詳細 |
| GET | `/api/events/{id}/ranking` | ランキング取得 |
| POST | `/api/events/{id}/scores` | スコア投稿（画像付き） |

#### 管理者向け（要認証）

| メソッド | パス | 機能 |
|---|---|---|
| GET | `/api/admin/songs/pending` | 未承認の選曲提案一覧 |
| POST | `/api/admin/songs/{id}/approve` | 選曲提案を承認 |
| POST | `/api/admin/songs/{id}/reject` | 選曲提案を却下 |
| POST | `/api/admin/events` | イベント作成 |
| GET | `/api/admin/scores/pending` | 未承認スコア一覧 |
| POST | `/api/admin/scores/{id}/approve` | スコア承認（画像削除） |
| POST | `/api/admin/scores/{id}/reject` | スコア却下 |

### バリデーション方針

**フロント側**: UX向上のための即時チェック
**サーバー側（必須）**: セキュリティ・データ整合性の確保

ASP.NET Coreのモデルバリデーション属性で実装:

```csharp
public class SongSubmissionRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Song { get; set; }
    
    [Required]
    [RegularExpression(@"^(NOV|ADV|EXH|MXM|INF|GRV|HVN|VVD|XCD|ULT)$")]
    public string Effect { get; set; }
    
    [Required]
    [RegularExpression(@"^[A-Za-z0-9_]{1,15}$")]
    public string XId { get; set; }
    
    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string SubmitName { get; set; }
    
    [StringLength(500)]
    public string SubmitReason { get; set; }
}
```

### X ID の正規化

入力されたX IDは以下のように正規化してDB保存:

- `@abc_player` → `abc_player`
- `https://x.com/abc_player` → `abc_player`
- `https://twitter.com/abc_player` → `abc_player`
- 全角@は半角に変換
- 小文字化

### Nginxでのサイズ制限

```nginx
client_max_body_size 10M;  # 画像アップロード用
```

## ディレクトリ構成

```
weepa/
├── .gitignore
├── .env.example              # 環境変数サンプル（秘密情報はここ）
├── README.md                 # セットアップ・運用手順
├── docker-compose.yml        # 本番用
├── docker-compose.dev.yml    # 開発用オーバーライド（ホットリロード等）
│
├── api/                      # ASP.NET Core プロジェクト
│   ├── Dockerfile
│   ├── Weepa.Api.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── Controllers/
│   │   ├── HealthController.cs
│   │   ├── SongsController.cs
│   │   ├── EventsController.cs
│   │   ├── ScoresController.cs
│   │   └── AdminController.cs
│   ├── Models/
│   │   ├── Entities/         # DBエンティティ
│   │   ├── Requests/         # リクエストDTO
│   │   └── Responses/        # レスポンスDTO
│   ├── Data/                 # DbContext等
│   ├── Services/             # ビジネスロジック
│   └── Validation/           # 入力検証
│
├── web/                      # 静的フロント
│   ├── index.html            # トップページ（現在開催中のイベント・ランキング）
│   ├── submit.html           # 選曲提案フォーム
│   ├── score.html            # スコア投稿フォーム
│   ├── admin.html            # 管理者ページ
│   ├── css/
│   │   └── style.css
│   └── js/
│       ├── main.js
│       ├── submit.js
│       ├── score.js
│       └── admin.js
│
├── nginx/
│   └── conf.d/
│       └── default.conf      # Nginx設定
│
├── db/
│   └── init/
│       └── 01_schema.sql     # 初期化SQL（CREATE TABLE + トリガー + インデックス）
│
└── deploy/
    ├── deploy.sh             # サーバーへのデプロイスクリプト
    └── README.md             # デプロイ手順
```

## docker-compose.yml（案）

```yaml
services:
  db:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - db_data:/var/lib/postgresql/data
      - ./db/init:/docker-entrypoint-initdb.d:ro
    networks:
      - internal
    # 外部公開しない
  
  api:
    build: ./api
    restart: unless-stopped
    depends_on:
      - db
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Default: "Host=db;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
    volumes:
      - uploads:/app/uploads
    networks:
      - internal
    # 外部公開しない
  
  nginx:
    image: nginx:alpine
    restart: unless-stopped
    depends_on:
      - api
    ports:
      - "80:80"
      # 将来SSLを入れる時に443を追加
    volumes:
      - ./nginx/conf.d:/etc/nginx/conf.d:ro
      - ./web:/var/www/html:ro
      - uploads:/var/www/uploads:ro
    networks:
      - internal

volumes:
  db_data:
  uploads:

networks:
  internal:
    driver: bridge
```

## .env.example

```env
POSTGRES_DB=weepa
POSTGRES_USER=weepa_user
POSTGRES_PASSWORD=change_me_please
```

## Nginx設定（default.conf 案）

```nginx
server {
    listen 80;
    server_name _;
    
    client_max_body_size 10M;
    
    # 静的ファイル
    root /var/www/html;
    index index.html;
    
    # 承認待ち画像の配信
    location /uploads/ {
        alias /var/www/uploads/;
        expires 1h;
    }
    
    # API
    location /api/ {
        proxy_pass http://api:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
    
    # SPA的に動かす場合はここでフォールバック
    location / {
        try_files $uri $uri/ /index.html;
    }
}
```

## デプロイ方針

### 開発フロー

```
[ローカル Windows + Docker Desktop]
  開発・動作確認
  ↓ git push
[GitHub (private repo)]
  ↓ サーバー側で git pull
[Ubuntu サーバー]
  docker compose up -d --build
```

### デプロイスクリプト（deploy/deploy.sh）

```bash
#!/bin/bash
# ローカルから実行するデプロイスクリプト
set -e

SERVER_USER="your_user"
SERVER_HOST="your.server.ip"
APP_PATH="/path/to/weepa"

ssh "${SERVER_USER}@${SERVER_HOST}" "cd ${APP_PATH} && git pull && docker compose up -d --build"
```

### サーバー初期セットアップ手順

1. Ubuntu サーバーにSSH（鍵認証）で接続
2. Docker / Docker Compose はインストール済み
3. プロジェクトを clone
   ```bash
   git clone git@github.com:your-account/weepa.git
   cd weepa
   ```
4. `.env` ファイルを作成（`.env.example` をコピーして秘密情報を入力）
5. 初回起動
   ```bash
   docker compose up -d --build
   ```
6. 動作確認
   ```bash
   curl http://localhost/api/health
   ```

### 将来的に追加予定

- 独自ドメイン設定
- Let's Encrypt でSSL化（certbot コンテナを追加）
- バックアップの自動化（pg_dump）

## 実装の段階（ステップ）

### Step 1: 最小構成で疎通確認（最優先）

- ディレクトリ構成を作る
- docker-compose.yml で3コンテナ起動
- API: `/api/health` が `{"status": "ok"}` を返すだけ
- フロント: `index.html` が「Hello Weepa」と表示するだけ
- nginx: 静的ファイル配信 + `/api/*` をapi コンテナへプロキシ
- **ゴール**: `docker compose up -d` でブラウザから `http://localhost` が見え、フロントからAPIを呼べる

### Step 2: DBスキーマ投入 + DB接続

- 上記 CREATE TABLE 文 + トリガー + インデックスを `db/init/01_schema.sql` に
- ASP.NET Core から PostgreSQL に接続（Npgsql または Entity Framework Core + Npgsql）
- `/api/health` で「DB接続OK」を返すように拡張
- **ゴール**: DB連携まで確認

### Step 3: 選曲提案機能

- `POST /api/songs` 実装
- `GET /api/songs/search?q=xxx` オートコンプリート実装
- 選曲提案フォーム（submit.html）実装
- バリデーション（サーバー側）
- X IDの正規化処理
- **ゴール**: フォームから投稿できる

### Step 4: イベント機能 + ランキング表示

- `GET /api/events/current`
- `GET /api/events/{id}/ranking`
- トップページ（index.html）で現在開催中のイベント+ランキング表示
- 管理者が開催を作成する機能（簡易でOK）
- **ゴール**: 開催を設定すると公開ページで見える

### Step 5: スコア投稿機能（画像アップロード付き）

- `POST /api/events/{id}/scores` 実装（multipart/form-data で画像付き）
- 画像保存先: `/app/uploads/pending/{event_id}/{uuid}.jpg`
- スコア投稿フォーム（score.html）実装
- 画像のサイズ制限、形式チェック（JPEG/PNG のみ等）
- **ゴール**: フォームから画像付きでスコア投稿できる

### Step 6: 管理者画面と承認フロー

- 管理者認証（Basic認証 or Cookie）
- `GET /api/admin/songs/pending`
- `POST /api/admin/songs/{id}/approve` / `reject`
- `GET /api/admin/scores/pending`
- `POST /api/admin/scores/{id}/approve` / `reject`
- 承認時に画像削除処理
- 管理者ページ（admin.html）実装
- **ゴール**: 投稿→承認→公開 の一連の流れが動く

### Step 7: デプロイ

- GitHubにpush
- サーバーで git clone
- `.env` を準備
- `docker compose up -d --build`
- 動作確認
- デプロイスクリプト（deploy/deploy.sh）作成

### 将来拡張（Step 8以降）

- SSL化（Let's Encrypt）
- 3曲通しスコアタ対応（別テーブル）
- 年末集計機能（Twitter IDベース）
- X ID統合機能
- reCAPTCHA導入
- OCRでスコア自動入力
- お知らせ機能

## 補足: 保留事項と将来の検討事項

### スコア投稿の更新ポリシー（保留中）

- 現状: 同じ人が同イベントで複数回投稿できる前提（submit_idで識別）
- 履歴を全部残す方針
- 承認前投稿が複数並立する場合の扱いは運用で決める

### 選曲提案の自動発行（保留中）

- 当初「週1で自動発行」と記載していたが、**管理者が手動でイベント作成**する方針で開始
- 自動化は運用が落ち着いてから検討

### 楽曲マスタの統合

- 表記ゆれ対策は:
  1. フロントでオートコンプリート（既存候補からの選択を誘導）
  2. X IDと同じく、管理者画面で「この曲とこの曲は同じ」として統合する機能（将来）

## 用語集

| 用語 | 意味 |
|---|---|
| スコアタ | スコアアタックの略 |
| エフェクト | SDVXの譜面種別（NOV/ADV/EXH/MXM/INF系/ULT） |
| X ID | Twitter（X）のユーザー名、`@` を除いた部分 |
| 選曲提案 | ユーザーが課題曲を提案すること |
| 開催（イベント） | 提案から選ばれた課題曲でスコアタを実施する期間 |
| 承認フロー | 投稿された選曲・スコアを管理者がチェックして公開する流れ |

## 参考: SDVXの譜面種別

- NOV (Novice)
- ADV (Advanced)
- EXH (Exhaust)
- MXM (Maximum)
- INF系（曲により呼称が異なる）
  - INF (Infinite) - SDVX II
  - GRV (Gravity) - SDVX III
  - HVN (Heavenly) - SDVX IV
  - VVD (Vivid) - SDVX V
  - XCD (Exceed) - SDVX VI EXCEED GEAR
- ULT (Ultimate) - 現状 Everlasting Message 1曲のみ、Lv.20

---

## Claude Code への指示

上記仕様書に基づいて、以下の順で実装してください。

### 最初のタスク: Step 1（最小構成で疎通確認）

1. ディレクトリ構成を作成
2. 3コンテナ構成の `docker-compose.yml` を作成
3. 最小限の ASP.NET Core プロジェクトを作成（`/api/health` のみ）
4. 最小限の `index.html`（「Hello Weepa」表示 + API呼び出しテスト）
5. Nginx設定ファイルを作成
6. `.gitignore`, `.env.example`, `README.md` を作成
7. ローカルで `docker compose up -d --build` して動作確認できる状態にする

各ステップごとに動作確認しながら進めてください。
各Stepを完了するごとに「次のStepに進んでいいか」を確認してください。

### 重要な制約

- **DB直アクセスは想定しない**（APIが唯一の入口）
- **バリデーションはAPI側で必須**（フロントは補助）
- **.NET 8 を使用**
- **PostgreSQL 16 を想定**
- **Windows ローカル開発 + Ubuntu サーバー本番**
- **秘密情報（DB パスワード等）は `.env` 管理、Git には含めない**
