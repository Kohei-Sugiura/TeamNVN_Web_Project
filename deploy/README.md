# deploy/

本番デプロイ関連のスクリプト置き場。

## SSL 化 (Let's Encrypt)

### 事前準備

1. ドメインを取得し、A レコードでこのサーバの公開 IP を指す
2. ポート 80 / 443 が外部から到達可能 (Let's Encrypt の HTTP-01 challenge は 80 を叩いてくる)
3. `.env` に下記を追記:

   ```
   DOMAIN_NAME=teamnvn.example.com
   LETSENCRYPT_EMAIL=admin@example.com
   ```

4. `WEB_PORT=80` に切り替え (開発用 19080 だと Let's Encrypt が来られない)

### 初回発行

```
docker compose up -d
./deploy/setup-ssl.sh
```

スクリプトの動作:

1. nginx 経由で `/.well-known/acme-challenge/` が外部から到達可能か事前確認
2. certbot を webroot モードで実行 → `/etc/letsencrypt/live/$DOMAIN_NAME/` に cert 保存
3. `nginx/conf.d/default.conf.ssl-template` を `$DOMAIN_NAME` で埋めて `default.conf` を上書き (元ファイルは `.bak.<timestamp>` に退避)
4. `nginx -t` で設定検証 → `nginx -s reload`

### 更新 (毎日 cron)

```
crontab -e
```

```
30 3 * * * /home/nvn_operator/workspace/web/deploy/renew-ssl.sh >> /var/log/letsencrypt-renew.log 2>&1
```

certbot は「30日以内に切れる cert のみ更新」する idempotent な作りなので、毎日叩いて問題ない。

### 元の HTTP 構成に戻すには

```
cp nginx/conf.d/default.conf.bak.<timestamp> nginx/conf.d/default.conf
docker compose exec nginx nginx -s reload
```
