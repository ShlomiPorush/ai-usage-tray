FROM node:24-alpine

ARG VERSION=dev

LABEL org.opencontainers.image.source="https://github.com/ShlomiPorush/ai-usage-tray" \
      org.opencontainers.image.description="AI Usage Tray remote-view server" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}"

WORKDIR /app

ENV NODE_ENV=production \
    PORT=8080 \
    DATABASE_PATH=/data/usage.db \
    WEB_ROOT=/app/web

COPY remote/server/package.json remote/server/server.mjs remote/server/vapid-configuration.mjs remote/server/generate-vapid-keys.mjs remote/server/docker-entrypoint.sh remote/server/healthcheck.sh remote/server/VERSION ./server/
COPY remote/shared ./shared
COPY web ./web

RUN apk add --no-cache su-exec \
    && addgroup -S -g 10001 remoteview \
    && adduser -S -D -H -u 10001 -G remoteview -s /sbin/nologin remoteview \
    && mkdir -p /data \
    && chown -R remoteview:remoteview /app /data \
    && chmod 0555 /app/server/docker-entrypoint.sh /app/server/healthcheck.sh

USER 0:0

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD ["/app/server/healthcheck.sh"]

ENTRYPOINT ["/app/server/docker-entrypoint.sh"]
CMD ["node", "server/server.mjs"]
