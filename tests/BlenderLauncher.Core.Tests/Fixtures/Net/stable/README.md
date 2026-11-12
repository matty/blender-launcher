# Stable fixtures

Recorded from download.blender.org on 11 Nov 2026 with `record.json` (see `../README.md`):

| File | What |
|---|---|
| `index.html` | `https://download.blender.org/release/` |
| `Blender4.5.html` | The 4.5 LTS folder |
| `Blender5.2.html` | The 5.2 LTS folder |
| `Blender5.3.html` | The 5.3 folder (newest) |

Only these three folders are recorded. Tests that start from an empty cache set the minimum version to 5.2 or 5.3. The test with the hosted seed (`Fixtures/upstream-api-stable_builds_api_windows.json`) uses minimum 4.2: on that day, 4.2, 4.3, 4.4, 5.0 and 5.1 had the same modified date as the seed, so only 4.5, 5.2 and 5.3 are fetched.

## Hand edits

- `index-modified.html` is a copy of `index.html` with one change: the `Blender5.2/` date is `11-Nov-2026 10:00` instead of `10-Nov-2026 08:38`. It is **not** in `manifest.json`. The cache-invalidation test adds it as a second response for the index URL, so the first scrape sees the recorded index and the second sees the newer 5.2 date.

Re-recording replaces the four recorded files. After that, re-create `index-modified.html` from the new `index.html` and check the dates the seed test relies on.
