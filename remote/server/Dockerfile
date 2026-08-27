FROM node:24-alpine

LABEL org.opencontainers.image.source="https://github.com/ShlomiPorush/ai-usage-tray" \
      org.opencontainers.image.description="AI Usage Tray remote-view server" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app

ENV NODE_ENV=production \
    PORT=8080 \
    DATABASE_PATH=/data/usage.db \
    WEB_ROOT=/app/web

COPY remote/server/package.json remote/server/server.mjs ./server/
COPY web ./web

RUN addgroup -S -g 10001 remoteview \
    && adduser -S -D -H -u 10001 -G remoteview -s /sbin/nologin remoteview \
    && mkdir -p /data \
    && chown -R remoteview:remoteview /app /data

USER 10001:10001

EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:8080/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"]

CMD ["node", "server/server.mjs"]
