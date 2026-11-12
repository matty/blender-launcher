# github-releases fixtures

Recorded on 11 Nov 2026 from upstream's repository, `Victor-IX/Blender-Launcher-V2`, because this project has no
releases yet. Unauthenticated (no `Authorization` header was sent or recorded). Used by `Updates/AppReleaseService*`.

Trimmed by hand after recording (re-recording overwrites these trims; repeat them):

- `releases-per-page-100.json`: 49 releases cut to 8 (`v2.8.2`, `v2.8.1`, `v2.8.0`, `v2.7.6`, `v2.7.1`, `v2.7.0`,
  `v2.7.0-rc.1`, `v2.6.1`), in the order GitHub returned them. Each release keeps only `url`, `html_url`, `id`,
  `tag_name`, `target_commitish`, `name`, `draft`, `prerelease`, `created_at`, `published_at`, `assets` and `body`
  (`author`, `reactions` and similar were dropped). Each release keeps two assets, the Linux and Windows zips, with
  only `name`, `content_type`, `size` and `browser_download_url`. Bodies are unchanged.
- `v2.8.2.body` (the HTML tag page that `/releases/latest` redirects to): replaced by a one-line placeholder; the
  launcher reads only that hop's URL, never its body.

Cases the live data does not show (a pre-release newer than every stable release, drafts, no releases, rate
limits) are built inline in the tests.
