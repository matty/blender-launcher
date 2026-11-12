# builder.blender.org fixtures

Recorded on 11 Nov 2026 with the recorder (`record.json`), then trimmed by hand. The live listings are large (daily 91 KB, daily/archive 1.3 MB, patch 834 KB, patch/archive 695 KB), so each body keeps only a representative subset of the real entries, unchanged:

| File | Entries kept |
|---|---|
| `daily.json` | 14 of 147: Windows amd64 zips (two stable, one alpha), two arm64 zips, an amd64 `.sha256`, `.msi` and `.msix`, two Linux `.tar.xz` and a `.sha256`, two macOS arm64 and one x86_64 `.dmg` |
| `daily_archive.json` | 5 of 2160: two amd64 zips, one arm64 zip, one Linux, one macOS |
| `patch.json` | 10 of 1283: amd64 zips for PR163602, PR155204, PR164649, PR162828 and PR166147 (chosen to sit on Gitea list pages 1, 2, 3 and on none; see `../gitea`), arm64 zips for two of them, a `.sha256`, one Linux, one macOS |
| `patch_archive.json` | 4 of 1056 |
| `experimental_archive.json` | as recorded (the live listing was empty) |

`experimental.json` is **hand-written**: the live listing was `[]` when recorded. Its five entries copy the shape of the real daily entries, with branches `npr-prototype` and `vulkan-dev` (one with `"patch": null`).

Re-recording overwrites the trimmed bodies; trim again (or adjust the tests) afterwards.
