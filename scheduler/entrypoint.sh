#!/bin/sh
set -eu

: "${ADMIN_BASIC_USER:?ADMIN_BASIC_USER is required}"
: "${ADMIN_BASIC_PASSWORD:?ADMIN_BASIC_PASSWORD is required}"
: "${AUTO_WEEKLY_TRIGGER_CRON:=5 0 * * *}"
: "${AUTO_WEEKLY_TRIGGER_URL:=http://api:8080/api/admin/events/auto-weekly}"

cat >/etc/crontabs/root <<EOF
SHELL=/bin/sh
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
${AUTO_WEEKLY_TRIGGER_CRON} /usr/local/bin/run-auto-weekly.sh
EOF

echo "[scheduler] cron installed: ${AUTO_WEEKLY_TRIGGER_CRON}"
echo "[scheduler] target: ${AUTO_WEEKLY_TRIGGER_URL}"

exec crond -f -l 2
