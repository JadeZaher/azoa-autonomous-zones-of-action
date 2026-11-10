# Brand marks — Aevani, PlantGeo, OVERSEASGENAI, ArdaNova, AZOA, NEOS

Date: 2026-11-09 (America/Denver). Built to live on the new LinkedIn pages and as site favicons. Visual system notes for PrimusNeo itself are in `primusneo-style.md` (live site + JadeZaher/primusneo-next `src/styles/globals.css`, inspected, not cloned).

## Family rules

PrimusNeo has no pictorial logo. Identity is typographic: `PRIMUS—NEO` in Archivo semibold, open tracking, plus a single accent period in `#4D6BFF`. The family marks follow that construction:

- Square on ink `#0B0C0E`
- Figure in paper `#E8E9EB` (OverseasGenAI uses its warmer foam `#F2ECE1`)
- One accent, used like PrimusNeo's period — a single note, not a wash
- Flat, geometric, no gradients, no shadows, no texture. OverseasGenAI's mark is all curves (owner brief: a swirl wave, no square seal).
- Must hold at 48px and 32px, and in a circular LinkedIn crop
- Wordmarks are set in Archivo 600 with a tracked all-caps line and the accent period. Text was set in type, not generated, so it does not garble.

Accents, so the three marks can sit in a row without colliding:

| Brand | Accent | Role |
|---|---|---|
| PrimusNeo | `#4D6BFF` | the period |
| Aevani | `#8FD8B4` mint (from aevani.com) | the crossbar |
| PlantGeo | `#4D6BFF` (shared with PrimusNeo) | the slab's edge |
| OVERSEASGENAI | `#D9542E` seal (from overseasgenai.com) | the period on the wordmark only; the mark itself is all foam |
| ArdaNova | `#EF4638` signal (from the live brand guidelines) | the period |
| AZOA | `#A63D15` rust (from the live marketing site) | the action node, and the period |
| NEOS | paper `#F2EDE3` on black | the square period in the official N |

ArdaNova, AZOA, and NEOS keep their own live palettes rather than the PrimusNeo ink ground: ArdaNova is parchment `#F4EFEB` / ink `#151513` / signal `#EF4638`; AZOA is paper `#F2EDE3` / ink `#16120D` / rust `#A63D15`; NEOS keeps its official cream typographic N on black (`#F2EDE3` / `#000000`). Construction is the same: flat, hard geometry, one accent used like a period, no gradients, and it must hold at 32px and in a circular LinkedIn crop. Wordmarks are set in the brand's live type, not generated.

Winners were redrawn as exact-palette vectors (SVG + PNG). GenerateImage JPEGs were treated as concepts only: their hex values drifted, and JPEG is a poor master for a logo.

---

## Aevani

Field-tested hydroponic, aquaponic, silvopasture, and agroforestry systems. "Tested, not listed." The live site already has an organic two-leaf sprout on a rounded forest-green square (`#1C3527` / `#8FD8B4` / `#F4F1EA`). That mark was not redrawn. The new work honors PrimusNeo's high-contrast typographic system instead.

### Concepts

**A — typographic A (winner).** A sharp geometric capital A in paper, with a short mint bar floating in the counter, not quite touching the legs. The bar is the family period. The letter also reads as a greenhouse gable or trellis.

- Advocate: Immediately "Aevani." Typographic kinship with `PRIMUS—NEO`. Holds at 32px and 48px with no loss of the mint bar. Architectural rather than botanical, which fits "tested, not listed." No text to misspell.
- Adversary: Geometric A monograms are common (a generic startup A, a distant cousin of various sharp-A marks). The disconnected bar could be read as a minus or a progress chip; at 32px it still reads as a crossbar. No political load, no leaf-in-a-circle, no AI artifacts in the reconstruction. Survives.

**B — stacked layers (runner-up).** Three horizontal bars, longest at the bottom, top bar mint. Hydroponic trays or canopy / understory / ground.

- Advocate: Extremely legible at small sizes. Not a leaf. Clean family geometry.
- Adversary: Reads as a hamburger menu, a layers icon, or generic SaaS "stack" mark. Weak specificity unless you already know the story. Kept as the strongest runner-up, not the winner.

**C — vertical tower (rejected).** A rounded-rectangle frame with three slats, top slat mint.

- Advocate: Directly references Aevani's vertical grow tower.
- Adversary: At 48px it is a battery icon (charge at ~30%). Also a fridge, a server rack, a window. Rejected.

### Why A won

It is the only concept that is both a distinctive identity (a letter, like PrimusNeo's wordmark culture) and bulletproof at favicon size, without looking like a UI icon or a plant cliché. Production files are a geometric reconstruction of the generated concept, snapped to `#0B0C0E` / `#E8E9EB` / `#8FD8B4`.

---

## PlantGeo

Open-source 3D mapping for wildfire prevention and ecosystem monitoring. The live page is still a "Loading map…" shell with no real mark. Wildfire work must not use flame or orange-alert imagery.

### Concepts

**A — terrain fold (runner-up, not paired with Aevani A).** Two flat facets meeting at a ridge, paper and `#4D6BFF`.

- Advocate: Beautiful, immediate "3D terrain," holds at 48px, no map pin.
- Adversary: A house roof, a chevron, an up-arrow, a generic mountain. And it is a peaked gable — the same silhouette as Aevani's A. Side by side they would look like one mark in two colors. Not used as the winner for that family reason.

**B — contour + benchmark (rejected for small sizes).** Two irregular contour rings and a blue survey square.

- Advocate: The most literally "geo," and the square (not a teardrop pin) is the right benchmark.
- Adversary: At 48px the wobble disappears and it becomes a target, a radar, or an eye. Softer than PrimusNeo's typographic sharpness. The irregularity can look like an unfinished blob.

**C / E — map tiles (rejected).** A 2×2 grid with one cell raised as a cube (C) or filled flat (E).

- Advocate: Clear "the map becomes a model."
- Adversary: Four panes are uncomfortably close to the Windows logo. The cube picked up Web3 / NFT associations and muddied at 48px. Thin strokes in E lost contrast when shrunk.

**D — isometric plane (winner).** A 2:1 isometric map-tile slab: paper top, thickness in `#4D6BFF`, no extra shading.

- Advocate: "The land as a surface," which is exactly 3D mapping. Distinct from Aevani's peaked A. Two colors, no gradient, thick enough that the blue edge survives at 32px. No pin, no globe, no flame.
- Adversary: Isometric slabs are used by many SaaS and "platform" brands, and can look like a floor tile or a credit card. The generated version had an irregular pentagon top (an AI geometry error); the winner is a reconstructed, regular rhombus so the left edge is clean. Not a cube, so it avoids the Codesandbox / Web3 box. No political reading.

**F — elevation profile (runner-up).** A four-segment terrain section with a blue benchmark on the main ridge.

- Advocate: "Terrain as data," very editorial.
- Adversary: A stock chart (unhelpful next to finance/crypto posting). The second peak smears at 32px. Kept as a runner-up, useful maybe on a wide cover, not as a square avatar.

### Why D won

It is the only PlantGeo concept that stays distinct from Aevani's A, holds at 32px, and still says "3D map" without a pin, a Windows grid, or a fire glyph. Production files are the reconstructed regular slab, not the generated JPEG.

---

## OVERSEASGENAI

Tagline: "Ventures, built to cross oceans." Seven-wave process, built with Escherbridge.

Owner feedback on the first mark (a foam hook with a square vermilion seal): "just a swirl with all curves for the wave." That version was moved to `runner-ups/overseasgenai-concept-a-crest-with-seal.png`. The redo is a single continuous curling wave, curves only — no square seal, no straight segments, no hard corners. It has to read as a wave, not a hook, a question mark, or the letter C or G, and it has to hold in a circular LinkedIn crop and at 32px.

Ink ground `#0B0C0E`, foam stroke `#F2ECE1`. A vermilion accent was allowed only if it was also curved; it was tried and rejected (see V3).

### Swirl concepts (round 2)

**V1 — even archimedean swirl (runner-up).** A constant-width spiral of about 1.6 turns, tail exiting lower right.

- Advocate: Clean, all curves, no corners, clearly a swirl, holds at 32px.
- Adversary: The right-hand tail makes it read as a 6, a G, or a generic koru/@ more than a wave. Orientation is the problem; the form is good. Kept as a runner-up.

**V2 / V2c — log-spiral wave (winner).** A logarithmic spiral, slightly loosened and stretched on the outer arm so it comes in from the lower left as a wave body, then curls over and spirals inward about 1.7 turns. Mild taper (110→90). Foam only. V2c opened the inner eye a little so the center survives at 32px.

- Advocate: One continuous stroke, all curves, no seal, no corners. The left-hand tail + inward curl is a breaking wave, not an open hook (the earlier failing form). At 48px and in a circle crop the swirl is obvious; at 32px the eye and the tail both still read. Distinct from Aevani's peaked A and PlantGeo's slab.
- Adversary: Any spiral risks a 6, an e, an @, a Maori koru, or a hurricane symbol. The long horizontal wave-arm and the slight stretch keep it from being a centered cyclone or an @. It is closer to a koru than the old hook was, but a koru is usually a tight circular fern frond; this one has a wave body. Not a question mark, not a C, not a G (the inner turn completes the swirl). No political reading. No AI artifacts — it is a constructed spiral, not a traced JPEG.

**V3 — vermilion inner curl (rejected).** Same path as V2, with the innermost turn in `#D9542E`.

- Advocate: The accent is curved, as allowed, and ties the mark to the site's seal color.
- Adversary: The red core clogs the eye and reads as a kidney, a comma, a chili, or a blood-drop in the wave. At 32px it is just a red blot. Rejected; vermilion stays on the wordmark period only.

**V2b and a two-ribbon "nami" (rejected).** V2b's tail dropped to the lower right and became a G/hook again. A double-ribbon wave (in the spirit of a generated two-tail breaking wave) produced intersections and a bitten hole in the center, and it was not "a single swirl."

### Why V2c won

It is the only variant that is just a swirl, is made only of curves, and still reads as a wave — especially at 32px and in a circle — without a square, a hook, or a letterform.

The wordmark remains `OVERSEAS—GENAI.` with a vermilion period. The live site currently sets the name as one closed word; the em dash is a family choice.

## ArdaNova

A cooperative project-and-work platform. Live tagline, verified on https://ardanova-client-production.up.railway.app (og:description and twitter:description, 2026-11-09): "Find a problem. Fund it. Build it. Share in what it becomes, on agreed terms." The leftover favicon was a 48×48 "T3" ICO, unrelated to the brand; it is filed under `refs/ardanova-current-favicon.png`. Brand guidelines (Escherbridge/Ardanova `documentation/BRAND_GUIDELINES.md`) call for flat neobrutalism, parchment `#F4EFEB`, ink `#151513`, signal red `#EF4638`, square corners, typography-led, no glow or glass. "Own what you build" is aspirational and is not used in any of these assets. No coins, tokens, charts-going-up, or investment imagery.

### Concepts

**AN ligature (winner).** A flat-top block A with a vertical right stem, ligatured to a block N, plus a signal-red square period. Parchment ground. The A is not a peaked gable.

- Advocate: Immediately "ArdaNova." Typography-led, which is what the guidelines ask for. The flat cap and the joined N keep it distinct from Aevani's pointed A on ink. The red period is the family accent. Holds at 32px and in a circular crop; the A counter and the period both survive. No text to misspell in the mark itself (the letters are constructed geometry, not a generated wordmark).
- Adversary: Letter ligatures can be misread (AW, FN, a windowed building, a belt-buckle A). The slanted left leg and the open upper counter keep it an A, and the N's diagonal is clipped to the letter box so it does not spike. Not a coin, a chart, or a rising-sun mark. No political reading. The first construction of this ligature (a spiked cap and a protruding diagonal) was rejected and rebuilt.

**Two records (runner-up).** Two unequal vertical ink columns (5:3, so they are not a pause icon) bound by a signal-red square. "Two records. One agreement."

- Advocate: Extremely simple, holds at 32px, tells a product story without letters, no family collision with Aevani's A or NEOS's N.
- Adversary: Still adjacent to a pause button, an 11, a plug, or an align-tool icon unless you already know the two-record story. Kept as the strongest runner-up.

**Brief / header bar (rejected).** An ink plate with a thick signal-red cap.

- Advocate: Bold, neobrutalist, "a problem brief."
- Adversary: A flag, a calendar, a browser window, or just a black square with a red hat. The red is a field, not a period. At 32px it has no identity. Rejected.

### Why the AN ligature won

It is the only concept that is both a typographic identity for ArdaNova and bulletproof at favicon size, without looking like a UI icon, a flag, or Aevani's A. Production files use the exact parchment / ink / signal tokens. The OG uses the live tagline verbatim.

---

## AZOA

An open-source engine for durable, strictly structured financial workflows (Apache-2.0, pre-launch, simulated rails). Live marketing site: https://azoa-frontend-production.up.railway.app. Title "AZOA — Autonomous Zones of Action." Meta description: "One engine. Every kind of economy." Palette taken from the live landing CSS: paper `#F2EDE3`, ink `#16120D`, rust `#A63D15`. Type on the site is Space Grotesk + IBM Plex Mono; wordmarks follow that. The brief was a simple high-contrast engine mark and a share image that looks like infrastructure, not a consumer token brand. No coins, charts, wallets, or "LIVE / FIAT-BRIDGED" language on the assets.

### Concepts

**Zone with an action node (winner).** A thick ink square housing and a rust square node inset in the lower-right interior.

- Advocate: Reads as a zone, a chassis, or an engine bay — infrastructure, not a consumer app. Two colors, hard corners, the rust node is the family period. Holds at 32px and in a circle; the node stays visible. Distinct from ArdaNova's letters and NEOS's N.
- Adversary: A picture frame, a QR finder pattern, a stamp, or a checkbox. The frame is much thicker than a QR finder, and the node is offset (QR finders are concentric and centered). Not a token, a yield chart, or a chain link. No famous-logo likeness.

**Solid plate with a rust corner (runner-up).** An ink square with a rust node inset at the lower right.

- Advocate: Even more bulletproof at 32px; very industrial.
- Adversary: A packing label, a "badge" notification, or a generic black square. Less "engine" than the housing. Kept as the strongest runner-up.

**Open chassis / C-channel (rejected as the winner).** The zone frame with the right side removed, rust node inside.

- Advocate: Breaks the QR reading; looks like a machine channel.
- Adversary: It is a letter C, or a magnet. Rejected as the winner.

**Orthogonal action graph (rejected).** Two ink nodes and a rust end, thick bars, L-shaped.

- Advocate: Literal "action graph."
- Adversary: A hammer, a faucet, a dumbbell, or a generic workflow-SaaS node cluster. Fragile at 32px. Rejected.

**Block Z with a rust terminal (rejected).** A heavy constructed Z.

- Advocate: Typographic, machine-like, holds at size.
- Adversary: A standalone Latin Z has an unintended political reading (the 2022 military "Z" symbol). The first construction also grew miter spikes. Rejected.

### Why the zone won

It is the only mark that says "engine / zone" at 32px without becoming a letter, a tool, a token, or a political Z. Production files use the exact paper / ink / rust tokens. The OG uses the live description, not launch claims.

---

## NEOS

A governance operating system and visual workspace, live at https://neos.primusneo.com. Title: "NEOS — Governance in Practice." Description: "A visual workspace for learning, discovery, collaboration, and participatory governance." The existing typographic N (cream `#F2EDE3` on black `#000000`, plus a square period) was kept, not redrawn. It was pulled from the live favicon at `/favicon.svg` and scaled on the official 32-unit grid to 1024. Design system: Signal Paper / Black Index, Inter + JetBrains Mono, hard corners, no gradients. No OmniOne, Bali, token, or "fundable" language appears on any of these assets.

### Why the official N was kept

The brief was to keep the existing typographic N and give it a LinkedIn-ready square, a paper-and-ink OG, apple-touch / favicon sizes, and a cover. The live mark already holds at 32px and in a circular crop; inventing a new one would have broken continuity with the site. The OG and cover are paper-and-ink compositions around that mark, set in Inter and JetBrains Mono, using the live title and description.

---

## What was rejected in general

- Leaf-in-a-circle, teardrop map pins, globes, compass roses, flames
- Gradients, soft 3D lighting, AI texture, misspelled lettering (no generated wordmarks were kept)
- Marks that collide with Aevani's peaked A (PlantGeo fold, OverseasGenAI mountain, a peaked-A ArdaNova)
- Marks that look like UI icons (hamburger, battery, pause) or famous four-pane / cube logos
- Political or national readings (rising-sun discs, alert-orange wildfire marks, a standalone military-adjacent Z)
- Coins, tokens, charts-going-up, wallets, and other investment or consumer-crypto imagery (ArdaNova, AZOA)

## File list

Official masters (square, 1024×1024, well above LinkedIn's 300×300):

- `/workspace/gtm/brand/aevani-logo.png`
- `/workspace/gtm/brand/plantgeo-logo.png`
- `/workspace/gtm/brand/overseasgenai-logo.png`
- `/workspace/gtm/brand/ardanova-logo.png`
- `/workspace/gtm/brand/azoa-logo.png`
- `/workspace/gtm/brand/neos-logo.png`

Vector sources: `svg/aevani-logo.svg`, `svg/plantgeo-logo.svg`, `svg/overseasgenai-logo.svg`, `svg/ardanova-logo.svg`, `svg/azoa-logo.svg`, `svg/neos-logo.svg`.

Favicons (32 and 180, plus 48 and 192 working copies) live in `favicons/`.

Open Graph images (1200×630): `/workspace/gtm/brand/ardanova-og.png`, `azoa-og.png`, `neos-og.png` (also copied under `og/`).

LinkedIn covers (1128×191):

- `covers/aevani-cover-1128x191.png`
- `covers/plantgeo-cover-1128x191.png`
- `/workspace/gtm/brand/overseasgenai-cover.png` (also copied under `covers/`)
- `/workspace/gtm/brand/ardanova-cover.png`, `azoa-cover.png`, `neos-cover.png` (also under `covers/`)

Wordmark lockups and type-only wordmarks are in `wordmarks/`. Aevani, PlantGeo, and OVERSEASGENAI are set in Archivo 600. ArdaNova is Inter, AZOA is Space Grotesk, NEOS is Inter; kickers are JetBrains Mono or IBM Plex Mono. No generated lettering.

Best runner-ups:

- Aevani: `runner-ups/aevani-concept-b-layers.png` (stacked trays). The generated A is in `runner-ups/aevani-concept-a-monogram-generated.png` for comparison with the vector winner.
- PlantGeo: `runner-ups/plantgeo-concept-a-fold.png`, `runner-ups/plantgeo-concept-f-profile.png`, and the generated (irregular) plane in `runner-ups/plantgeo-concept-d-plane-generated.png`.
- OVERSEASGENAI: the previous crest-and-seal mark is `runner-ups/overseasgenai-concept-a-crest-with-seal.png`. Swirl runner-ups: `runner-ups/overseasgenai-swirl-v1-even.png` (even spiral, tail to the right) and `runner-ups/overseasgenai-swirl-v3-vermilion-core.png` (red inner curl, rejected). Earlier non-swirl concepts remain: `overseasgenai-concept-b-horizon.png`, `overseasgenai-concept-c-wake.png`.
- ArdaNova: `runner-ups/ardanova-concept-two-records.png` (best RU). The brief/header-bar is `runner-ups/ardanova-concept-brief.png` (rejected). The leftover live "T3" favicon is in `refs/`.
- AZOA: `runner-ups/azoa-concept-plate.png` (best RU). Rejected: `azoa-concept-chassis.png` (reads as a C), `azoa-concept-graph.png` (hammer/faucet), `azoa-concept-z.png` (political Z risk).
- NEOS: no new pictorial runner-ups; the official N was kept.



Reference: live OverseasGenAI OG and favicon in `refs/`. Palette and type for PrimusNeo in `primusneo-style.md`.
