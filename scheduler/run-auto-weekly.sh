#!/bin/sh
set -eu

response_file=$(mktemp)
cleanup() {
  rm -f "$response_file"
}
trap cleanup EXIT

http_code=$(curl -sS -o "$response_file" -w "%{http_code}" -u "$ADMIN_BASIC_USER:$ADMIN_BASIC_PASSWORD" -X POST "$AUTO_WEEKLY_TRIGGER_URL")
body=$(cat "$response_file")
timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

case "$http_code" in
  200|201|409)
    echo "[scheduler] $timestamp status=$http_code body=$body"
    exit 0
    ;;
  *)
    echo "[scheduler] $timestamp status=$http_code body=$body" >&2
    exit 1
    ;;
esac
