# formap v8 — format change log & RM / SD reader-sync document

**Status:** v8 writer + verification tooling implemented & bit-exact verified on real data (LZ4, −44% on warminsko).
Done: init-state reader → v8 · hard-cut · README · full-Poland v8 + init-state · OsmConverter split · Ed25519 signing · **signed `poland-v8.bin` built & verified** (`--sign-existing`). **v8 format is FINAL.** Pending: RM/SD v8 readers · deploy (drop signed files into RM).
(Per-change "Status: planned" lines below are superseded by the **Change-completion log** at the bottom — that is the current truth.)
**Audience:** formap (writer) · RM (reader, Unity / C#) · SD (reader, C++)
**Purpose:** single source of truth for **every** v8 binary-format change.

formap has **no automated format tests** — the writer (formap) ↔ reader (RM, SD) contract
is kept in sync **by hand**. Therefore:

> **After implementing each change below, fill in its "Done / reader impact" entry and
> share this document with RM (and SD) BEFORE they touch their readers.**
> This is the mechanism that stops us silently desyncing the format.

---

## Golden rules for v8

1. **No OSM data removed.** Every feature, tag, layer and field that v7 carries, v8 carries.
2. **No coordinate-precision change.** Vertices stay `float32`, bit-exact.
3. Every change is **bit-exact reversible** — lossless re-encoding only.
4. v8 is a **coordinated bump**: formap + RM + SD must agree bit-for-bit. Bump the magic/version
   so a mismatched reader fails **loudly**, not silently.

---

## Scope — three pillars

- **Pillar 1 — SD compatibility (additive).** One hard change: the header carries **`LayerCount`
  and `LODCount`**, and readers read them **dynamically** (never assume compile-time constants).
  This kills the v7 latent trap where a 12- vs 13-layer file desyncs a reader that assumes a fixed
  count (it bit our own analyzer immediately). Other SD needs = optional CLI flags, later.
- **Pillar 2 — signing.** **Ed25519, sign-only** (provenance + integrity). ODbL-clean — a signature
  restricts nothing, so it is **not** a TPM. **No encryption**, no anti-tamper on OSM data, online
  verification deferred. Tracked as a separate workstream from size.
- **Pillar 3 — size.** **Strictly lossless** re-encoding + compression. No drops, no precision loss.
  First-wave target: **~21.5 GB → ~9–11 GB (~2×)**, with **zero latency risk**.

---

## Measured baseline — production `RM-0.2/.../Maps/Poland/poland-v7.bin` (21.5 GB, 13 layers)

Uncompressed body 34.3 GB; on-disk 21.5 GB → compression is only **1.52×**.

| category | % of uncompressed body |
|---|---:|
| indices (`int32`) | 49.0% |
| vertices (`2×float32`) | 35.8% |
| metadata (keys + values) | 7.1% |
| counts (6 × `int32` per feature) | 4.7% |
| bbox (per feature) | 3.2% |

- Geometry = **84.8%** of the body. Metadata only 8.1%.
- LOD duplication = **3.25×** — **kept by design** (self-contained per-LOD blocks → lag-free LOD
  switching; RM re-reads one block per switch). v8 keeps this; we only make each copy leaner.
- Metadata: **214M string occurrences, only 272,644 unique** strings (427 distinct keys).
- `holeStarts` = 0 bytes across all of Poland; bbox is derivable from vertices.

(Authoritative v7 layout: `BinaryFormat.cs` + `MeshGeometry.cs`. v8 deltas are documented per-change below.)

---

## First-wave changes (agreed: 1, 2, 4, 5)

### Change 1 — Compression: LZ4 `L00_FAST` → LZ4-HC
- **What:** recompress each tile LOD block at an LZ4-HC level (done once, in formap).
- **Format / layout change:** **NONE** — same LZ4 pickle format, same `LZ4Pickler` API.
- **Reader impact (RM / SD):** **NONE** — same decoder, same call. Load time unchanged or slightly
  better (less I/O). No new dependency.
- **Status:** planned.
- **Done / reader impact:** _(fill after implementation)_

### Change 2 — Indices: `int32` → `uint16` (+ per-feature wide fallback)
- **What:** store index values as `uint16`. A feature with > 65,535 vertices falls back to `int32`,
  flagged per feature.
- **Format / layout change:** index element size 2 B (default) or 4 B (flagged). The width flag lives
  in the Change-5 bitfield. (Consider the same for `HoleStarts` / `SegmentIds` / `JunctionIndices` — TBD.)
- **Reader impact (RM / SD):** read the width flag, parse `uint16`/`int32` accordingly. Values identical to v7.
- **Status:** planned. **TODO:** measure max vertices/feature to confirm how rarely the `int32` fallback fires.
- **Done / reader impact:** _(fill)_

### Change 4 — Metadata string table
- **What:** one per-file table of unique strings; each feature's metadata becomes `(keyIndex, valIndex)`
  varint pairs instead of inline UTF-8.
- **Format / layout change:** new string-table section (count, then each unique UTF-8 string once).
  Per-feature metadata = varint count + that many `(keyIdx, valIdx)` varints.
- **Reader impact (RM / SD):** load the table **once** at map-open, then resolve indices to strings.
  **Every tag preserved** — no metadata dropped. Load time neutral/better (index lookup vs UTF-8 parse).
- **Status:** planned.
- **Done / reader impact:** _(fill)_

### Change 5 — Per-feature counts → presence bitfield + varint counts
- **What:** replace the six fixed `int32` counts with a 1-byte presence bitfield + varint counts for the
  arrays that are actually present. Also carries the Change-2 wide-index flag.
- **Format / layout change:** per-feature record starts with the bitfield byte, then conditional varint
  counts, then the arrays.
- **Reader impact (RM / SD):** parse bitfield, then the conditional counts. An absent array (bit off) ==
  v7's count = 0. Lossless.
- **Status:** planned.
- **Done / reader impact:** _(fill)_

### Header change (Pillar 1 — do alongside the first wave)
- **What:** add explicit `LayerCount` and `LODCount` fields to the header (room in the reserved bytes).
- **Reader impact (RM / SD):** read them dynamically; never assume 12/13 layers or 6 LODs. Permanently
  removes the layer-count desync trap.
- **Status:** planned.
- **Done / reader impact:** _(fill)_

---

## Pillar 2 — Ed25519 signature (sequenced LAST)

### Change S — file signature
- **What:** sign the finished v8 file; RM / SD verify on load and refuse to load on mismatch
  (provenance + integrity).
- **Scope (decided):** Ed25519, **sign-only**, **no encryption**. ODbL-clean — a signature restricts
  nothing, so it is not a TPM. Public key embedded in RM / SD; private key never in the repo. Online
  verification deferred to a later version.
- **Sequencing:** implemented **after** the size/encoding changes — signs the final, stabilized bytes,
  so no rework.
- **Format change / detailed design (benchmarked 2026-06-08):** hash cost is cheap & one-time — SHA-256 is
  HW-accelerated at **~2,440 MB/s** (441 MB v8 → 181 ms; ~12 GB poland v8 → ~5 s pure CPU); SHA-512 ~3× slower;
  Ed25519 itself ~tens of µs (runs on the 64-byte hash, size-independent).
  ⚠ **But whole-file signing fights RM's streaming** — RM reads tiles on-demand via the index, NOT the whole
  file; a whole-file signature would force reading all ~12 GB at open just to hash it (a new ~5–24 s I/O cost).
  **Recommended design:** store a **per-tile (per-block) hash in the tile index** and **Ed25519-sign the index**.
  At open: verify the signed index (~MB, ms — RM reads it anyway) → authenticates the index + all tile hashes.
  During streaming: hash each block and compare to its trusted index hash (~0.18 ms/tile). → full integrity +
  provenance, **no whole-file read, ~zero load-time impact.** (Sign the index with its tile-hash field zeroed, or
  sign over a canonical serialization that excludes the signature bytes.)
- **Reader impact (RM / SD):** the v8 index now ALWAYS carries a 32-byte SHA-256 per LOD block (after the 6
  LODInfos, before featureCounts) — readers must read/skip these `lodCount × 32` bytes per entry **even for
  unsigned files**. If header `signatureLength == 64`: read the trailing 64-byte signature, Ed25519-verify it
  over the index byte region with the embedded public key at open (reject on fail); then per streamed block,
  SHA-256 the compressed bytes and compare to the index hash (reject on mismatch).
- **Status:** ✅ DONE & verified (2026-06-14).
- **Done:** `Signing.cs` (BouncyCastle Ed25519 — GenerateKeypair / Sign / Verify); per-LOD SHA-256 hashes in the
  index; optional trailing 64-byte Ed25519 signature over the index; header `signatureLength` field; CLI
  `--gen-key <path>`, `--sign-key <priv>`, `--verify-sig <file> <pub>`. Verified: build 0/0, selftest green,
  signed warminsko → `[VERIFY-SIG] PASS` (2,639 blocks, 0 mismatch); tamper a block byte → block-hash FAIL;
  tamper the index → signature FAIL; `--init-state-only` still reads the signed file (5052 railways / 6059 POIs).
  Private key supplied via CLI (file path), never in the repo; public key to be embedded in RM/SD.

---

## Refactor (part of v8): split the `OsmConverter` god-class — ✅ DONE (2026-06-14)

**Done:** extracted three cohesive **static** groups out of the 2,833-line god-class (pure move, no logic change):
`LayerClassifier.cs` (14 tag/relation predicates), `PolygonUtils.cs` (6 geometry-math helpers),
`LodFilter.cs` (`CreateLODLevel1–5` + road/place helpers — now shared by both writers). `OsmConverter.cs`
2,833 → 2,245 lines. Verified: build 0/0, `--selftest` green, and a warminsko regen reproduced **all
run-invariants exactly** (5052 railways / 6059 POIs / 38,733 nodes / 77,558 edges / 148 stations / 384 platforms;
FORMAP04 ~462 MB). The parse/build/write pipeline core stays in `OsmConverter` as the orchestrator.



`OsmConverter.cs` is ~2,722 lines doing everything — parse, project, triangulate, classify layers, LOD-filter,
write. Break it into focused units (e.g. `OsmParser`, `GeometryBuilder`, `LayerClassifier`, `LodFilter`, the v8
writer/reader). New v8 code is born in its own files (`FeatureCodecV8`, `BinaryFormatV8`) so the god-class does
not grow meanwhile. **Sequencing:** land + verify the v8 format first, then do the split as a separately-verified pass.

⚠ **The safety net is NOT byte-identical output.** formap's output is **non-deterministic** across runs: polygon
ways are processed in parallel (`OsmConverter.cs` ~2:60), so feature order within a layer varies, and railway
`segmentId` values are assigned by processing order — two runs of the *same* input produce different bytes (and
different segment numbers) with identical data. Verify a behavior-preserving refactor with an **order-independent,
segmentId-tolerant feature-multiset comparison** (see `--verify-v8`, which sorts features by a content key), or a
**single-run round-trip** (see `--verify-write`), NOT `cmp`/hash of two files.

---

## Deferred (NOT in the first wave)

- **Change 3 — coordinate stream layout** (delta + structure-of-arrays + byte-shuffle): lossless and
  precision-safe, but **adds decode-time CPU** (nibbles the load-time budget) and is the most complex.
  Revisit only if the first wave isn't small enough; **round-trip test mandatory**.
- **Zstd** (instead of / after LZ4-HC): better ratio (~3–4×) but slower per-block decode + a new
  dependency in all three readers. Revisit after measuring LZ4-HC.
- ~~**bbox per-feature: KEPT**~~ → **DROPPED** (see completion log): derived from vertices on read, bit-exact,
  ~0 load cost. Saved −8.4% of the file. Change 6 revised.

---

## Decisions

- **Magic / version — DECIDED:** magic `FORMAP04`, version `8` (next in sequence: `FORMAP01`/v5 →
  `FORMAP03`/v7 → `FORMAP04`/v8).
- **Migration — DECIDED: hard-cut, no dual-write.** formap stops emitting v7 and emits v8 only.
  No v7↔v8 overlap is needed because **no map will be regenerated for RM or SD during the transition** —
  the existing v7 file + v7 readers keep running untouched until v8 is complete end-to-end (writer +
  both readers), then Poland is regenerated as v8 and RM + SD switch readers together.
- **Pillar 2 (signing) — DECIDED: last.** Done after the size/encoding changes — it signs the finished
  v8 file, so the layout stabilizes first and there is no rework. Detailed design happens when we reach it.
- **Compression — DECIDED: v8 = LZ4-HC (−44%).** Zstd (−54%) measured & implemented but **deferred to v9/v10**
  (slower decode + ZstdSharp/libzstd dependency + IL2CPP validation). Header `compressionType` field makes the
  switch forward-compatible.

---

## Change-completion log

_Append one dated line per completed change: what shipped, any deviation from plan, and the reader action
required of RM / SD._

**In progress** (core implemented & unit-verified; pipeline integration + readers still pending — do **not**
update RM/SD readers yet, layout may still shift until a change is marked shipped):

- 2026-06-08 — **v8 header** (`WriteHeaderV8` / `ReadHeaderV8` in `BinaryFormat.cs`): magic `FORMAP04`,
  version 8, 128-byte header with explicit `LayerCount` + `LODCount`. Builds clean; not yet emitted by a writer.
- 2026-06-08 — **per-feature codec** (`FeatureCodecV8.cs` — covers Changes 2 + 4 + 5): presence-bitfield +
  varint counts; indices `uint16` with per-feature `int32` fallback (flag bit 5); metadata as string-table
  indices. bbox kept; vertices raw `float32` (precision untouched). **Round-trip self-test PASSES** — 6 cases
  incl. fractional coords (bit-exact), >65 536-vertex wide-index path, polygon-with-hole, railway
  seg/junction ids, metadata. Run via `formap --selftest`.
- 2026-06-08 — **v8 container** (`BinaryFormatV8.cs` — `WriteV8` / `ReadV8`): 128-B header + one global string
  table + per-tile/per-LOD **LZ4-HC** blocks (Change 1), located via the index (dropped v7's redundant per-block
  length prefix + its `+4` footgun) + v8 tile index. **Full-file round-trip self-test PASSES** (synthetic tiles
  × 6 LODs, incl. `layerMask` / empty-LOD handling).
- 2026-06-08 — **first wave wired into the real pipeline & VERIFIED LOSSLESS on real data**
  (`WriteBinaryV8` in `OsmConverter.cs`, `--format v8`, `--read-v8`). Generated from `warminsko-mazurskie.osm.pbf`:
  **v8 657.9 MB vs v7 786.6 MB (same input) = −16.4%.** Feature count (2,516,881) and vertex count (59,822,370)
  match v7 **exactly** → losslessness confirmed on real data.
  ⚠ **Projection correction:** the ~2× target was computed on the *uncompressed* body. LZ4 was already
  compressing away most of what uint16 / bitfield / string-table remove (zero-heavy int32 indices, repeated
  strings, zero counts), so v8's denser body compresses at ~1.3–1.4× vs v7's 1.62× and the *on-disk* gain is
  only ~16%. **Dominant residual = float32 vertices (~478 MB uncompressed, barely compressible, precision-locked).**
  → Remaining size now lives in the deferred levers: **Change 3 (delta + SoA + byte-shuffle — targets the vertex
  floats)** and **Zstd**. To be measured before committing. Not yet shipped as default (still opt-in `--format v8`;
  hard-cut + init-state reader + README pending).
- 2026-06-08 — **Change 3 (vertex SoA + byte-shuffle) implemented & VERIFIED BIT-EXACT on real data.**
  Block layout changed: per-feature *structure* (bbox, flags, counts, indices, metadata refs) is separated from
  the vertices, which are pooled per block, SoA-split (X-plane | Y-plane) and byte-shuffled (stride 4) before
  LZ4-HC. Result on `warminsko-mazurskie.osm.pbf`: **v8+C3 481.3 MB vs v7 786.6 MB = −38.8% (1.63×)** (Change 3
  alone cut the first-wave v8 file a further −27%). Losslessness proven three ways: codec self-test (bit-exact
  incl. fractional precision, wide-index, segmentIds), real-data count match, and a **same-run round-trip of all
  2,516,881 features with 0 mismatches** (`formap … --verify-write`). Decode stays fast LZ4 (inverse shuffle
  ~2 GB/s). **Zstd rejected** by benchmark (2.50× ratio but ~3× slower decode — load-latency risk in Unity).
  **Reader impact (RM/SD):** after LZ4-decompressing a block, parse the structure section, then un-byte-shuffle
  the X and Y planes, un-SoA, and distribute vertices to features by their structure vertexCounts.
  Still opt-in `--format v8`; finalization (hard-cut, init-state reader, README) pending.
- 2026-06-08 — **bbox-drop (revises Change 6) — implemented & VERIFIED BIT-EXACT on real data.** The per-feature
  bbox is no longer stored; it is recomputed from vertices on read (`ComputeBoundingBox`, min/max — derivable,
  so not "data"). Result on `warminsko-mazurskie.osm.pbf`: **v8 C3+bbox-drop 440.9 MB vs v7 786.6 MB = −44.0%
  (1.78×)** — bbox-drop alone cut a further −8.4% (min/max floats compress poorly, so the win is bigger than the
  ~4% estimate). `--verify-write`: all 2,516,881 features 0 mismatches, incl. the recomputed bbox → confirmed
  derivable & bit-exact. ~0 load cost (RM already recomputes bounds via `RecalculateBounds`).
  **Reader impact (RM/SD):** do NOT read a per-feature bbox; recompute it from the feature's vertices if needed
  (e.g. AdminBoundaries point-in-polygon quick-reject). **This is the lossless + fast-LZ4 ceiling: −44%.**
- 2026-06-08 — **Zstd option measured (aggressive path).** Added `--compress zstd` (ZstdSharp.Port in formap; the
  compression type is now a v8 header field so the file self-describes — reader dispatches LZ4/Zstd). Zstd-19 on
  `warminsko-mazurskie.osm.pbf` (C3 + bbox-drop): **359.0 MB vs v7 786.6 MB = −54.4% (2.19×)** — −18.6% below the
  LZ4 file, clears the −50% goal. `--verify-write`: 2,516,881 features, 0 mismatches (Zstd round-trip bit-exact).
  **Trade vs LZ4:** decode ~3× slower (1,499 vs 4,276 MB/s) BUT RM streams **1 tile/frame on the main thread**, so
  the cost is spread → no in-game hitch. Costs: managed-Zstd dependency (RM) + libzstd (SD) + IL2CPP decode
  validation; Zstd-19 compress is slow (~2 min warminsko → ~30+ min poland — use a mid level 12–15 for builds:
  still >−50%, decode unchanged since Zstd decode speed is ~level-independent).
  **DECIDED (2026-06-08): v8 ships LZ4-HC (−44%, fast decode, no game-reader dependency).** Zstd (−54%) is
  implemented + measured but **reserved for v9/v10.** The v8 header's `compressionType` field stays (always
  `0`=LZ4 in v8), so enabling Zstd later is a forward-compatible change (set `1`; RM/SD add a Zstd decode path
  then). A v8 reader seeing `compressionType != 0` must error clearly, not misparse. The `--compress zstd` path
  + ZstdSharp stay in formap (build tool only; does not affect the game readers) ready to flip on for v9/v10.
- 2026-06-14 — **Init-state reader → v8 (finalization step 2) — DONE & verified.** `InitStateBuilder` now
  dispatches on magic: FORMAP04 → `BinaryFormatV8.ReadLogicLayersV8` (memory-bounded — per-tile LOD0 decode via
  `DecodeBlock`, keeps only Railways/AdminBoundaries/Places/POIs/Platforms/Coastlines); FORMAP03 → the existing
  v7 path (old files still read). Verified on warminsko: init-state from the v8 file vs the v7 file — ALL
  run-invariant outputs identical (5052 railways, 6059 POIs, 38,733 nodes, 77,558 edges, 1,919 junctions, 148
  stations, 384 platforms). Block-section counts differ (v8 30,085 vs v7 30,001) but that is **pre-existing
  run-non-determinism**, not the reader: a 2nd fresh v7 run gave 29,671 — block-section count varies v7→v7 by
  more than the v7↔v8 gap (node-ID assignment depends on parallel feature-processing order). v8 reader is
  bit-exact (`--verify-write`). Note: full-Poland init-state build will transiently decode all LOD0 layers per
  tile (DecodeBlock decodes the whole block) — bounded per tile; optimize later if build time matters.
- 2026-06-14 — **Hard-cut to v8 default (step 1) + README (step 3) — DONE.** `Program.cs`: default
  `formatVersion` 7→8; usage text updated (v8 default, v7 legacy via `--format v7`). **End-to-end verified:**
  `formap warminsko.osm.pbf` with default flags → `Output format: v8`, writes `FORMAP04`, then builds init-state
  reading the v8 file (38,733 nodes, 77,558 edges, 148 stations; init-state-pl.bin 5.47 MB). The v7 writer is kept
  as legacy `--format v7` (low-risk; also the reference for the OsmConverter split). README updated: usage,
  `--format v8|v7`, Output-format section (FORMAP04 + string table + pooled SoA/byte-shuffle vertices, ~44%,
  bbox derived), link to `docs/format-v8.md`. **Writer-side finalization complete; v8 is the production default.**
- 2026-06-14 — **Full-Poland v8 generated & validated end-to-end.** From `poland-260613.osm.pbf` (newest extract):
  **poland-v8.bin = 11.19 GB** (FORMAP04, 5776 tiles, ~42 min build) vs production v7 22.5 GB → **~2× smaller
  despite newer/larger data** (apples-to-apples −44% from warminsko). Init-state built from the v8 file at scale:
  `ReadLogicLayersV8` works across all 5776 tiles → 130,337 railways / 61,009 POIs / 48,518 places / 23,568
  platforms; graph = 570,969 nodes, 1,149,864 edges, 49,532 junctions, 3,381 stations, 10,628 platforms,
  339,934 block sections; init-state-pl.bin 83.7 MB (cf. production 76 MB — newer data). Build ~6m37s (one-time;
  the v8 init-state read decodes all LOD0 layers per tile — optimizable to skip non-logic materialization if
  build time matters). Files in `D:/Gry/formap/`, **not yet deployed to RM** (deploy when RM/SD v8 readers ship).
- 2026-06-14 — **OsmConverter split + Ed25519 signing DONE → v8 format is FINAL.** (Details in the Refactor
  section and Change S above.) ⚠ **The signing change altered the v8 index layout** (added always-present per-LOD
  SHA-256 hashes, +`lodCount×32` B/entry; entry 204 → 396 B at 13 layers / 6 LODs). So v8 files generated BEFORE
  this — the experimental `poland-v8.bin` (11.19 GB) and the `wm-*` test files — are now **stale (old index
  layout), unreadable by the current reader.** Pre-deploy, so fine: **regenerate poland-v8 (signed) at deploy
  time** with the current code. The v8 format (size encoding + signing) is complete & stable — RM/SD implement
  readers against this spec.
- 2026-06-14 — **`--sign-existing` added + signed `poland-v8` produced & verified.** New `--sign-existing
  <file> <priv>` signs an already-built v8 file in place (Ed25519 over the index region, set header
  `signatureLength`, append the 64-byte signature) — no ~50-min rebuild. Regenerated full-Poland v8 in the FINAL
  format (11.77 GB, `FORMAP04`) + init-state (79.8 MB), then signed it in place with a production keypair
  (`D:/Gry/keys/poland.{priv,pub}` — **off git**; `*.priv`/`*.pub` now gitignored). `--verify-sig` → PASS:
  SIGNATURE OK + 29,312 block hashes, 0 mismatch. Public key to embed in RM/SD:
  `8d045ef753730aa48e7e2118aa394ac104800ad117c804b5546d7c7fc74afbac`. Deployable artifacts in `D:/Gry/formap/`;
  remaining = RM/SD v8 readers + dropping the signed files in.
