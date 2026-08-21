# Remote view (static site)

The page that shows an AI Usage Tray snapshot from any device. Plain HTML, CSS and
JavaScript: no build step, no frameworks, no external requests other than the one
call to your own worker.

## Install

1. Copy this folder to the server, e.g. `/var/www/ai-usage/`.
2. Edit `config.js` and set `apiBase` to the worker URL (no trailing slash):

   ```js
   window.REMOTE_VIEW_CONFIG = { apiBase: "https://usage.example.workers.dev" };
   ```

3. Serve the folder as static files:

   ```nginx
   root /var/www/ai-usage;

   location / {
       try_files $uri $uri/ /index.html;
   }

   # .webmanifest is absent from nginx's default mime.types, so it would
   # otherwise be served as application/octet-stream and ignored.
   location = /manifest.webmanifest {
       default_type application/manifest+json;
   }

   # The service worker must never be cached, or an old shell keeps
   # reinstalling itself after a deploy.
   location = /sw.js {
       add_header Cache-Control "no-store";
   }
   ```

   The `.png` icons need no special handling. HTTPS is required for the service
   worker (and therefore for installing the page as an app); over plain HTTP the
   page still works, it just skips registration.

## Files

| File | Purpose |
| --- | --- |
| `index.html` | The page shell: header, theme toggle, notice slots. |
| `styles.css` | Both palettes. Light/dark follow the system unless `<html data-theme>` forces one. |
| `app.js` | Fetches the snapshot, renders the cards, owns the theme toggle and the landing view. |
| `config.js` | The one line you edit: `apiBase`. |
| `manifest.webmanifest` | Makes the page installable ("Add to home screen"). |
| `sw.js` | Service worker. Caches the static shell; never caches `/u/` responses. |
| `icon-192.png`, `icon-512.png` | App icons for the manifest, the favicon and iOS. |

All seven files must be reachable at the site root. The manifest, the service
worker and the icons are referenced by relative path, and a missing `sw.js` or
manifest makes the page uninstallable.

## Installing it as an app

On HTTPS the page registers `sw.js` and can be installed from the browser menu
("Install app" / "Add to Home Screen"). The manifest's `start_url` carries no id,
so `app.js` remembers the last id it saw in `localStorage` and reuses it when the
installed app is opened without one. Opening `/?id=` (an explicit empty id) always
shows the landing page instead, which is the way back out.

## Links

The app builds the link for you in **Settings → Remote view**. It has the form:

```
https://your-site/?id=<32-hex>
```

The id is a 32-character lowercase hex string and is the only credential, so treat
the link as a secret. Without a valid `id` the page shows a landing view with links
to the app's releases and repository.

The page refetches every 60 seconds and shows how long ago the app last reported.
Snapshots expire after about a week without the app running, and the page then
shows a "no data" message.
