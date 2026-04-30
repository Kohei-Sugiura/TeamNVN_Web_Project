#!/usr/bin/env bash
# Let's Encrypt 証明書を発行し、nginx を SSL 構成に切り替える。
#
# 前提:
#   1. .env に DOMAIN_NAME, LETSENCRYPT_EMAIL を設定済み
#   2. DOMAIN_NAME の A レコードがこのサーバの IP を指している
#   3. ポート 80 が外部から到達可能 (Let's Encrypt の HTTP-01 challenge 用)
#   4. docker compose up -d で nginx/api/db が稼働済み
#
# 使い方:
#   ./deploy/setup-ssl.sh
#
# 何度実行しても安全 (既存 cert がある場合は更新動作になる)。

set -euo pipefail

cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
    echo "ERROR: .env が見つかりません" >&2
    exit 1
fi

# .env 読み込み (export なし版に対応)
set -a
# shellcheck disable=SC1091
source .env
set +a

: "${DOMAIN_NAME:?ERROR: .env に DOMAIN_NAME を設定してください}"
: "${LETSENCRYPT_EMAIL:?ERROR: .env に LETSENCRYPT_EMAIL を設定してください}"

echo "==> ドメイン: $DOMAIN_NAME / メール: $LETSENCRYPT_EMAIL"

# 1. ACME challenge 経路の到達確認 (certbot コンテナで書き込み, nginx 経由で配信)
#    nginx 側の certbot_webroot mount は :ro なので、書き込みは certbot から行う。
echo "==> ACME challenge 経路の到達確認"
if ! docker compose ps --status=running --services | grep -qx nginx; then
    echo "ERROR: nginx コンテナが動いていません。先に 'docker compose up -d' してください" >&2
    exit 1
fi
TEST_TOKEN="setup-ssl-$(date +%s)"
docker compose run --rm --entrypoint sh certbot -c \
    "mkdir -p /var/www/certbot/.well-known/acme-challenge && echo ok > /var/www/certbot/.well-known/acme-challenge/${TEST_TOKEN}"

if curl -fsS --max-time 10 "http://${DOMAIN_NAME}/.well-known/acme-challenge/${TEST_TOKEN}" | grep -q '^ok$'; then
    echo "    OK: 外部から ${DOMAIN_NAME} → nginx に到達できます"
    docker compose run --rm --entrypoint sh certbot -c \
        "rm -f /var/www/certbot/.well-known/acme-challenge/${TEST_TOKEN}" >/dev/null
else
    docker compose run --rm --entrypoint sh certbot -c \
        "rm -f /var/www/certbot/.well-known/acme-challenge/${TEST_TOKEN}" >/dev/null || true
    cat <<EOF >&2
ERROR: http://${DOMAIN_NAME}/.well-known/acme-challenge/${TEST_TOKEN} に到達できませんでした
  - DOMAIN_NAME の DNS A レコードがこのサーバの公開 IP を指していますか?
  - ポート 80 が外部から開放されていますか?
  - nginx は HTTP(80) で起動していますか?
EOF
    exit 1
fi

# 2. 証明書を発行 (--keep-until-expiring で重複発行を回避)
echo "==> 証明書を発行/更新"
docker compose run --rm --entrypoint "" certbot \
    certbot certonly \
        --webroot -w /var/www/certbot \
        -d "$DOMAIN_NAME" \
        --email "$LETSENCRYPT_EMAIL" \
        --agree-tos \
        --no-eff-email \
        --keep-until-expiring \
        --non-interactive

# 3. SSL テンプレートをドメイン名で置換して default.conf に反映
echo "==> nginx 設定を SSL 化"
TEMPLATE=nginx/conf.d/default.conf.ssl-template
TARGET=nginx/conf.d/default.conf
BACKUP="${TARGET}.bak.$(date +%Y%m%d-%H%M%S)"

if [[ ! -f "$TEMPLATE" ]]; then
    echo "ERROR: $TEMPLATE が見つかりません" >&2
    exit 1
fi

cp "$TARGET" "$BACKUP"
echo "    既存設定を $BACKUP にバックアップしました"
sed "s/__DOMAIN__/${DOMAIN_NAME}/g" "$TEMPLATE" > "$TARGET"

# 4. nginx 設定検証 + reload
echo "==> nginx 設定検証"
if ! docker compose exec -T nginx nginx -t; then
    echo "ERROR: nginx 設定エラー。$BACKUP に戻します" >&2
    cp "$BACKUP" "$TARGET"
    exit 1
fi

echo "==> nginx reload"
docker compose exec -T nginx nginx -s reload

echo ""
echo "完了: https://${DOMAIN_NAME}/ にアクセスできれば成功です"
echo "更新は ./deploy/renew-ssl.sh を cron に登録してください (例: 毎日 03:30)"
