# Remote view (static site)

The page that shows an AI Usage Tray snapshot from any device. Plain HTML, CSS and
JavaScript: no build step, no frameworks, no external requests other than the one
call to your own remote-view server.

## Install

1. Copy this folder to the server, e.g. `/var/www/ai-usage/`.
2. Edit `config.js` and set `apiBase` to the API URL (no trailing slash):

   ```js
   window.REMOTE_VIEW_CONFIG = { apiBase: "https://usage-api.example.com" };
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
| `index.html` | The page shell: header, percentage and theme toggles, notice slots. |
| `styles.css` | Both palettes. Light/dark follow the system unless `<html data-theme>` forces one. |
| `usage.js` | The Usage tab: reads the shared usage report and renders the cost and token analytics. Loaded before `app.js`. |
| `app.js` | Fetches the snapshot, renders the cards, owns the persisted percentage/theme toggles and the landing view. |
| `config.js` | The one line you edit: `apiBase`. |
| `manifest.webmanifest` | Makes the page installable ("Add to home screen"). |
| `sw.js` | Service worker. Caches the static shell; never caches `/u/` responses. |
| `icon-192.png`, `icon-512.png` | App icons for the manifest, the favicon and iOS. |

All eight files must be reachable at the site root. The manifest, the service
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
https://your-site/?id=<32-hex read id>
```

The id in the link is the **read id**: a 32-character lowercase hex string that can
only fetch the snapshot. The app keeps a separate secret **write id** and never puts
it in a link, so sharing a link cannot let anyone overwrite or delete your data. The
read id is still a capability, so treat the link as unlisted rather than public.
Without a valid `id` the page shows a landing view with links to the app's releases
and repository. See `../remote/server/README.md` for how the two ids relate.

`?id=demo` is the one non-hex id the page accepts: the server answers it with a
sample payload, and the page marks it with a "Demo data" badge. It is deliberately
not remembered as the last-seen id, so opening the demo cannot hijack an installed
app that was set up with a real link.

The percentage toggle changes both the displayed numbers and meter fills between
used quota and capacity left. The first visit follows the preference uploaded by
the desktop app; choosing a mode on the page stores a browser-specific override.
Warning colours preserve the same capacity state in both modes. The page refetches
every 60 seconds and shows how long ago the app last reported.
Snapshots expire after about a week without the app running, and the page then
shows a "no data" message.

## Usage tab

When the desktop app shares its usage report (Settings, Remote view, "Share usage
on the remote view", off by default), the page shows a **Quotas | Usage** switch
in the header. The Usage tab mirrors the desktop Usage window: past 24 hours,
7, 30 or 90 days or custom dates, an account filter, a comparison with the
previous period or with picked dates, cost or tokens, a chart per agent, the
headline tiles, a breakdown by model or by day (days in the last week open by
hour), and model details. `#usage` in the link opens the tab directly.

How the page finds the report:

- Every quota snapshot carries `usage: { shared, seq, checkedAt }` while sharing
  is on. When `seq` is set and newer than the report on screen, the page fetches
  `GET /u/{id}/usage`. A 404 or a network error is retried on the next snapshot
  poll; there is no separate loop.
- When the flag disappears, the report is cleared from memory and from the page,
  the tab is hidden and `#usage` falls back to the quotas.
- A relay without the usage route (the Cloudflare Worker) never sets the flag,
  so the tab never appears there.

What the numbers mean:

- Days and hours are the desktop's, in its time zone. The page never works out
  a local midnight itself: day starts come from the report's `hourDayStarts`.
- Days or hours outside what the report covers are left blank and labelled
  "Partial data", never shown as zero. A one to three day range older than the
  report's hourly week is shown by day, with a note.
- "Past 24 hours" is the window the desktop stored, labelled "as of" its time.
- "Usage report updated" follows the snapshot's `checkedAt`; a yellow banner
  appears when the desktop has not confirmed the report for 30 minutes.
- Cost is the API list-price estimate, not a subscription bill. Models without a
  known price show "unpriced" and are left out of the dollar figures.
