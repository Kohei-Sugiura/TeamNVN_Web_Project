# Weepa

SOUND VOLTEX 交流チームの週1スコアアタック（スコアタ）管理サイト。

## 構成

- フロント: HTML / CSS / JavaScript（素の構成）
- API: ASP.NET Core (.NET 8)
- DB: PostgreSQL 16
- プロキシ: Nginx
- scheduler: cron で自動作成 API を叩く補助コンテナ
- コンテナ: Docker Compose（nginx / api / db / scheduler）

## セットアップ（ローカル開発）

### 前提

- Docker Desktop がインストールされていること

### 手順

1. `.env` を用意

   ```bash
   cp .env.example .env
   # 必要に応じてパスワードを書き換える
   ```

2. 起動

   ```bash
   docker compose up -d --build
   ```

3. 動作確認（開発時はデフォルトで `WEB_PORT=19080`）

   - フロント: http://localhost:19080/
   - ヘルスチェック: http://localhost:19080/api/health

   本番 Linux で 80 番を使う場合は `.env` で `WEB_PORT=80` に変更。
   Windows は IIS/W3SVC が 80 を握っているため、開発環境では 19080 を既定にしている。

4. 停止

   ```bash
   docker compose down
   ```

5. DBボリュームも消す場合

   ```bash
   docker compose down -v
   ```

## ディレクトリ

```
.
├── api/       ASP.NET Core プロジェクト
├── web/       静的フロント（HTML/CSS/JS）
├── nginx/     Nginx設定
├── db/        PostgreSQL 初期化SQL
└── deploy/    本番デプロイ用スクリプト
```

## 週次イベント自動作成（Step 6 以降向け）

- API: `POST /api/admin/events/auto-weekly`
- 認証: Basic 認証（`ADMIN_BASIC_USER` / `ADMIN_BASIC_PASSWORD`）
- 動作: 承認済み楽曲からランダムで1曲選び、`.env` の週次設定に基づく「次回開催枠」を作成
- 重複期間のイベントが既にある場合は `409 Conflict`

設定（`.env`）:

- `AUTO_WEEKLY_TIMEZONE` 例: `Asia/Tokyo`
- `AUTO_WEEKLY_DAY_OF_WEEK` 例: `Wednesday`（または `3`）
- `AUTO_WEEKLY_HOUR` 例: `21`
- `AUTO_WEEKLY_MINUTE` 例: `0`
- `AUTO_WEEKLY_DURATION_HOURS` 例: `168`
- `AUTO_WEEKLY_TRIGGER_CRON` 例: `5 0 * * *`

例: 毎週水曜21:00（JST）開始、168時間開催

```dotenv
AUTO_WEEKLY_TIMEZONE=Asia/Tokyo
AUTO_WEEKLY_DAY_OF_WEEK=Wednesday
AUTO_WEEKLY_HOUR=21
AUTO_WEEKLY_MINUTE=0
AUTO_WEEKLY_DURATION_HOURS=168
```

手動実行例:

```bash
curl -sS -u admin:change_me_admin_password -X POST http://localhost/api/admin/events/auto-weekly
```

scheduler コンテナ:

- `scheduler` サービスがコンテナ内 cron で API を定期実行
- 実際の開催日時は `AUTO_WEEKLY_*` が決定
- cron は `AUTO_WEEKLY_TRIGGER_CRON` で「いつチェックを走らせるか」だけを決定

例: 毎日 00:05 にチェック実行

```bash
5 0 * * * curl -sS -u admin:change_me_admin_password -X POST http://localhost/api/admin/events/auto-weekly >/dev/null 2>&1
```

## 実装ステップ

仕様は `weepa-spec.md` 参照。現在は **Step 1（最小構成で疎通確認）** の段階。
