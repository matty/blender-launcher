# UPBGE fixtures

Recorded on 11 Nov 2026 with the recorder (`BL_RECORD_SOURCES=upbge`, see `../README.md`), without a GitHub token,
so the `X-RateLimit-*` headers show the anonymous limit of 60. No `Authorization` header is ever stored (the recorder
keeps only the headers listed in `../README.md`). Used by `Scraping/Upbge/UpbgeBuildSourceTests.cs`.

Trimmed by hand after recording:

- `releases.json`: `releases?per_page=100` returned 90 releases (510 KB). Kept 10, chosen to cover each rule:
  `weekly-build-108` and `-107` (two weeklies of 0.53), `weekly-build-50` (weekly not marked pre-release),
  `weekly-build-26` (0.45 alpha), `v0.50` (a `windows-x64.7z`), `v0.44`, `v0.36.1` (`windows` and `windows7`
  packages, checksum files), `v0.30` (upper-case `UPBGE-` names), `v0.2.5b` (below 0.30) and `v0.2.4a` (no assets).
  Each release keeps only `html_url`, `id`, `tag_name`, `name`, `draft`, `prerelease`, `created_at`,
  `published_at` and `assets`; each asset keeps `name`, `content_type`, `state`, `size` and
  `browser_download_url`. Dropped: `body`, `author`, `uploader` and the other URL and id fields. Order kept.
- `tags.json`: `tags?per_page=100` returned 100 tags (43 KB), all `weekly-build-*` except the last eight. Kept the
  four weekly tags above and `v3.0.0`, each with `name` and `commit`. The stable `v0.xx` tags are not on GitHub's
  first page, so stable builds have no hash, as with the live API.

`manifest.json` is as recorded (its `ETag` values refer to the untrimmed bodies).

Re-recording overwrites `releases.json` and `tags.json` with the full lists; trim them again as above.
