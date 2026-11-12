# Bforartists fixtures

Recorded on 11 Nov 2026 with the recorder (`BL_RECORD_SOURCES=bforartists`, see `../README.md`). Used by
`Scraping/Bforartists/BforartistsBuildSourceTests.cs`.

- `downloads.html` (125 KB): `https://downloads.bforartists.com/`, untrimmed.
- `devbuilds.html` (39 KB): `https://devbuilds.bforartists.com/`, untrimmed.
- `manifest.json`: the two pages plus a `HEAD` entry (status, `Content-Length`, `Last-Modified`, `ETag`) for each of
  the 56 Windows zips on the stable page, which is what a first scrape on Windows sends. No bodies are stored for
  `HEAD`.

Not trimmed. To refresh, run the recorder; if the stable page gains Windows zips, add a `HEAD` line for each to
`record.json` (the tests fail with "no recorded response" naming the missing URL) and update the counts in the
tests.
