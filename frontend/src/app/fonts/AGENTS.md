# Self-hosted fonts

`layout.tsx` loads these through `next/font/local`, not `next/font/google`, so `next build` never fetches
from Google Fonts. The E2E stack image (`frontend/e2e/stack/frontend.Dockerfile`) broke on 2026-10-26 with
nine Turbopack errors — `Module not found: Can't resolve '@vercel/turbopack-next/internal/font/google/font'`
from `NextFontGoogleFontFileReplacer` ("next/font/google queries have exactly one entry"), one per
Space Grotesk `src:` line — while the same lockfile built fine locally and in the Frontend-audit job. Nothing in
the repo or lockfile changed: the input that varied was Google's live CSS response, which a build-time fetch
makes part of the build. Self-hosting removes that input; the build is now offline and deterministic.

Files (latin subset only, the same subset the Google import requested; fetched 2026-10-26):

| File | Source | Weights |
| --- | --- | --- |
| `space-grotesk-latin-variable.woff2` | `fonts.gstatic.com/s/spacegrotesk/v22/…PbF4C_k3HqU.woff2` | variable, 300-700 (layout uses 400/500/700) |
| `ibm-plex-mono-latin-400.woff2` | `fonts.gstatic.com/s/ibmplexmono/v20/-F63…n1i8q131nj-o.woff2` | 400 |
| `ibm-plex-mono-latin-500.woff2` | `fonts.gstatic.com/s/ibmplexmono/v20/-F6q…JwlBFgsAXHNk.woff2` | 500 |

Both families are SIL OFL 1.1; the licences ship beside the files (`OFL-*.txt`) as the OFL requires on
redistribution. The files are unmodified from what Google Fonts serves. To add a weight or subset, fetch the
`css2` URL with a modern browser user agent, take the matching `/* latin */` block's `src`, and add a `src`
entry in `layout.tsx` — do not switch back to `next/font/google`.

Plex Mono sets `adjustFontFallback: false` because `next/font/local` only knows Arial / Times New Roman
metrics; the Tailwind `font-mono` stack (`ui-monospace, monospace`) is the fallback.
