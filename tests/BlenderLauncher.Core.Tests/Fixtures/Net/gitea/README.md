# projects.blender.org (Gitea) fixtures

Recorded on 11 Nov 2026 with the recorder (`record.json`), then trimmed by hand.

- `pulls_page1.json` to `pulls_page4.json`: the four list pages (`limit=50&sort=recentupdate`), 50 pull requests each. Each object is cut down to `number`, `title`, `state`, `html_url` and `updated_at`; everything else (bodies, users, branches, labels) was removed, taking each page from about 600 KB to 12 KB.
- `pull_162828.json`, `pull_166147.json`, `pull_163602.json`: single lookups, cut down the same way. 162828 and 166147 are on none of the four pages.
- `pull_9999999.json`: the real 404 body for a pull request that does not exist.

The tests rely on 163602, 155204 and 164649 being on pages 1, 2 and 3. Re-recording changes the pages (they are sorted by recent activity), so re-check those numbers and `../builder/patch.json` afterwards.
