# Remote view (static site)

The page that shows an AI Usage Tray snapshot from any device. Plain HTML, CSS and
JavaScript — no build step, no frameworks, no external requests other than the one
call to your own worker.

## Install

1. Copy this folder to the server, e.g. `/var/www/ai-usage/`.
2. Edit `config.js` and set `apiBase` to the worker URL (no trailing slash):

   ```js
   window.REMOTE_VIEW_CONFIG = { apiBase: "https://usage.example.workers.dev" };
   ```

3. Serve the folder as static files:

   ```nginx
   location / {
       root /var/www/ai-usage;
       try_files $uri $uri/ /index.html;
   }
   ```

## Links

The app builds the link for you in **Settings → Remote view**. It has the form:

```
https://your-site/?id=<32-hex>
```

The id is a 32-character lowercase hex string and is the only credential, so treat
the link as a secret. Without a valid `id` the page just explains where to get one.

The page refetches every 60 seconds and shows how long ago the app last reported.
Snapshots expire after about a week without the app running, and the page then
shows a "no data" message.
