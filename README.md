# Weepa

SOUND VOLTEX 交流チームの週1スコアアタック（スコアタ）管理サイト。

## 構成

- フロント: HTML / CSS / JavaScript（素の構成）
- API: ASP.NET Core (.NET 8)
- DB: PostgreSQL 16
- プロキシ: Nginx
- コンテナ: Docker Compose（nginx / api / db の3コンテナ）

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

## 実装ステップ

仕様は `weepa-spec.md` 参照。現在は **Step 1（最小構成で疎通確認）** の段階。
