#!/usr/bin/env bash
# Let's Encrypt 証明書の更新。cron に登録して定期実行する想定。
# certbot は内部で「30日以内に切れる cert のみ更新」する idempotent な作りなので、
# 毎日叩いて問題ない。
#
# crontab 例:
#   30 3 * * * /home/nvn_operator/workspace/web/deploy/renew-ssl.sh >> /var/log/letsencrypt-renew.log 2>&1

set -euo pipefail
cd "$(dirname "$0")/.."

docker compose run --rm --entrypoint "" certbot certbot renew --webroot -w /var/www/certbot --quiet

# 更新成功時のみ nginx reload (証明書ファイルが置き換わるため)
docker compose exec -T nginx nginx -s reload || true
