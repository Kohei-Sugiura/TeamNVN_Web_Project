FROM alpine:3.20

RUN apk add --no-cache curl

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY run-auto-weekly.sh /usr/local/bin/run-auto-weekly.sh

RUN chmod +x /usr/local/bin/entrypoint.sh /usr/local/bin/run-auto-weekly.sh

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
