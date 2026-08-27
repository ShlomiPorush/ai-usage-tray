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

COPY remote/server/package.json remote/server/server.mjs remote/server/docker-entrypoint.sh remote/server/VERSION ./server/
COPY web ./web

RUN apk add --no-cache su-exec \
    && addgroup -S -g 10001 remoteview \
    && adduser -S -D -H -u 10001 -G remoteview -s /sbin/nologin remoteview \
    && mkdir -p /data \
    && chown -R remoteview:remoteview /app /data \
    && chmod 0555 /app/server/docker-entrypoint.sh

USER 0:0

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD ["su-exec", "10001:10001", "node", "-e", "fetch('http://127.0.0.1:8080/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"]

ENTRYPOINT ["/app/server/docker-entrypoint.sh"]
CMD ["node", "server/server.mjs"]
