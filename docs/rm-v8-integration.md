# RM ← formap v8 — integration & deployment guide

**For:** the RM (Unity / C#) developer.
**Goal:** make RM read the new **v8 (`FORMAP04`)** map format and deploy the new, ~half-size signed map.
**Status:** the v8 format is **FINAL**. formap produces it by default. RM still reads v7 — this is the work to switch.
**Byte-level spec + rationale:** [`format-v8.md`](format-v8.md) is the authoritative source of truth; this doc is the *how-to* for RM.

---

## 0. TL;DR

- v8 is a **lossless** re-encoding of v7 — same data, same LOD/tiling/duplication, ~**44% smaller** on disk (Poland 22.5 GB → 11.2 GB), and **fast LZ4 decode** (no new decompressor).
- RM's job: **a v8 read path** (required) + **Ed25519 signature verification** (OPTIONAL — fine to defer to v9), then **swap the map file**.
- **You do not write the reader from scratch.** formap's `BinaryFormatV8.cs` + `FeatureCodecV8.cs` + `Signing.cs` are a **complete, working C# v8 reader** — port them into RM, mapping `formap.MeshGeometry` → RM's mesh types.
- Decode is **fast and main-thread-safe** — it fits your existing 1-tile-per-frame `LoadTilesCoroutine` with negligible extra cost (no Zstd; we benchmarked it and chose LZ4-HC specifically to keep your load time intact).

---

## 1. What changed (v7 `FORMAP03` → v8 `FORMAP04`)

Same tile grid (10 km), same 6 LODs, same 13 layers, same per-LOD self-contained blocks (LOD-switch latency unchanged). The *encoding inside* changed:

| | v7 | v8 |
|---|---|---|
| Header | 128 B, no layer/LOD count | 128 B, **carries `LayerCount`, `LODCount`, `compressionType`, `signatureLength`** |
| Metadata | inline UTF-8 per feature | **one global string table**; features hold varint indices |
| Indices | `int32` | **`uint16`** (per-feature `int32` fallback flag) |
| Per-feature counts | 6 × `int32` | **1-byte presence bitfield + varints** |
| bbox | stored per feature | **not stored — derive from vertices** |
| Vertices | inline `float32` X,Y interleaved | **pooled per block, SoA-split (X plane \| Y plane), byte-shuffled** (precision **unchanged**, still raw float32) |
| Block framing | `int32 length` prefix + pickle | **pickle only** (length is in the index) |
| Tile index | …LODInfo… + featureCounts | …LODInfo… + **per-LOD 32-byte SHA-256** + featureCounts |
| Signature | none | **optional trailing 64-byte Ed25519** over the index |

Everything is **bit-exact reversible** — no feature, tag, layer or coordinate is lost or changed.

---

## 2. The v8 read path (port from formap)

formap is also C#, so you can adapt its reader almost directly. Map `formap.MeshGeometry` → your `MeshGeometry`.

**Reference files in the formap repo:**
- `BinaryFormatV8.cs` → `ReadV8` (whole file), `DecodeBlock` (one tile block), `ReadLogicLayersV8` (LOD0-only — mirrors what your gameplay extractor needs), `VerifySignatureV8`.
- `FeatureCodecV8.cs` → `ReadFeatureStructure` (per-feature record), `ReadVarint`, `Unshuffle` (byte de-shuffle).
- `BinaryFormat.cs` → `ReadHeaderV8`.
- `Signing.cs` → `Verify` (Ed25519).

**Open sequence (per file):**
1. Read magic. `FORMAP04` → v8; `FORMAP03` → keep your existing v7 path (so old files still load during transition).
2. `ReadHeaderV8` → `tileSize, bounds, tilesX, tilesY, totalTiles, indexOffset, layerCount, lodCount, compressionType, signatureLength`. **Read `layerCount`/`lodCount` dynamically — never hard-code 12/13/6** (this is the §4.4 trap that bit us; v8 fixes it for good).
3. Read the **string table** (immediately after the header): `varint count`, then per string `varint byteLen` + UTF-8 bytes. Keep it in RAM for the file's lifetime.
4. Seek `indexOffset`, read the index: per tile — `TileID(i64) GridX(i32) GridY(i32) bounds(4×f32)`, then `lodCount × LODInfo(FileOffset i64, CompressedSize i32, UncompressedSize i32, LayerMask i32)`, then **`lodCount × 32-byte SHA-256`** (read these even if you don't verify), then `layerCount × i32 featureCounts`.

**Per tile-LOD block (the part that changed):**
1. Seek `LODInfo.FileOffset`, read `CompressedSize` bytes (**no length prefix** — drop the v7 `ReadInt32()` + the `+4`).
2. LZ4-decompress: `LZ4Pickler.Unpickle(bytes)` (K4os, already your dependency; `compressionType` is `0`=LZ4 for v8).
3. Decode the block body (= `DecodeBlock`):
   - `varint structLen`; the struct section is `structLen` bytes.
   - Walk the struct section: per layer `int32 layerType, int32 featureCount`; per feature, `ReadFeatureStructure`:
     - `flags` byte: bit0 hasIndices, bit1 hasHoleStarts, bit2 hasSegmentIds, bit3 hasJunctionIndices, bit4 hasMetadata, **bit5 wideIndices**.
     - `varint vertexCount`.
     - if hasIndices: `varint count` + `count × (uint16 | int32 if bit5)`.
     - if hasHoleStarts/SegmentIds/JunctionIndices: `varint count` + `count × varint`.
     - if hasMetadata: `varint count` + `count × (varint keyIdx, varint valIdx)` → look up the string table.
     - **No bbox in the record** — you'll fill it after vertices.
   - After the struct section, read the two vertex planes: `xShuf = next (totalVerts × 4) bytes`, `yShuf = next (totalVerts × 4) bytes`, where `totalVerts` = sum of all features' `vertexCount`.
   - `Unshuffle(xShuf, 4)` and `Unshuffle(yShuf, 4)` (inverse byte-shuffle, stride 4 — see `FeatureCodecV8.Unshuffle`, a cheap linear pass).
   - Distribute: walk features in order, take `vertexCount` floats from X and Y planes (`BitConverter.ToSingle`), build each feature's vertex list, then **recompute its bbox** (`RecalculateBounds` / min-max).

That's it — you now have the same `Dictionary<LayerType, List<MeshGeometry>>` per LOD that v7 gave you, feeding your renderer + extractor unchanged.

**bbox:** v8 doesn't store it. Your renderer already calls `mesh.RecalculateBounds()`, so rendering is unaffected. The only stored-bbox consumer was `AdminRegion.ContainsPoint` (voivodeship PIP quick-reject) — recompute the bbox there from the polygon's vertices (one min/max pass, at init).

**init-state:** if your gameplay graph is rebuilt from the `.bin` (not from `init-state-pl.bin`), mirror `ReadLogicLayersV8` — it reads only LOD0 + the logic layers (Railways/AdminBoundaries/Places/POIs/Platforms/Coastlines). In practice you load the precomputed `init-state-pl.bin` (regenerated by formap), so this path is the fallback. `ReadLogicLayersV8` was sped up on 2026-06-15 (skips non-logic layers without materializing them, and skips whole blocks with no logic layer) — output identical, format unchanged; see [§8](#8-formap-side-change-log--init-state-build-speedup-2026-06-15) if you port it.

---

## 3. Signature verification (OPTIONAL — fine to ship in v9)

**Not required to load v8.** A signed v8 map reads + renders perfectly *without* verifying — the reader just ignores the trailing 64-byte signature and doesn't check the hashes. The **only** mandatory signing-related bit is in §2: **read/skip the per-LOD index hashes** (`lodCount × 32` bytes per entry) so the index parse stays aligned — they're always present, signed or not. Embedding the public key + verifying below is the *enforcement* layer ("only maps you signed load; tampering rejected") — add it whenever (a later pass / v9 is fine).

When you enable it — provenance + integrity, cheap and one-time (the per-block hash check is ~0.18 ms/tile and rides your existing per-frame streaming):

1. Add a managed Ed25519 to RM — **BouncyCastle.Cryptography** works in Unity (IL2CPP); port `Signing.Verify`.
2. **Embed the public key** (32 bytes, from `formap --gen-key`) in RM as a constant `byte[]`.
3. At map open, if `signatureLength == 64`: read the trailing 64 bytes, `Ed25519.Verify(pubKey, indexBytes, sig)` over the index byte region. **Refuse to load on failure.**
4. While streaming each block: `SHA256(compressedBytes)` and compare to the index's per-LOD hash. **Refuse the tile on mismatch.**

(Verifying only the index signature already gives provenance at open; the per-block hashes add content-integrity during streaming. Both are in the format.)

---

## 4. Deployment — artifacts are built & signed

The signed v8 map is **already produced and verified** (`--verify-sig`: SIGNATURE OK, 29,312 blocks, 0 mismatch):

- `D:/Gry/formap/poland-v8.bin` — signed, `FORMAP04`, ~11.77 GB.
- `D:/Gry/formap/init-state-pl.bin` — ~79.8 MB.
- Keypair: `D:/Gry/keys/poland.priv` (**secret — kept off git by the owner**) + `poland.pub`.
- **Public key to embed in RM** (32-byte Ed25519, hex):
  `8d045ef753730aa48e7e2118aa394ac104800ad117c804b5546d7c7fc74afbac`

Steps:
1. **Embed the public key** above in RM as a `byte[32]` constant (used by `Ed25519.Verify`). If the keypair is ever rotated, re-embed the new public key.
2. **Drop the files in:** replace `Assets/StreamingAssets/Maps/Poland/poland-v7.bin` with `poland-v8.bin` + the new `init-state-pl.bin`. Either rename to `poland-v8.bin` + update `MapLoader.mapFileName`, or dispatch by magic and keep the path.
3. **Build + smoke-test** (§5).

**When the map data updates** (re-signing the new build):
- `formap <new.osm.pbf> poland-v8.bin --sign-key D:/Gry/keys/poland.priv` (convert + sign in one run), **or**
- `formap <new.osm.pbf> poland-v8.bin` then `formap --sign-existing poland-v8.bin D:/Gry/keys/poland.priv` — **signs an already-built map in place in seconds, no ~50-min rebuild.**

---

## 5. Verification checklist (RM side)

- [ ] Map loads; magic-dispatch picks the v8 path for `FORMAP04`, v7 path still works for old files.
- [ ] Renders identically to v7 (all layers, all LODs; LOD switching has no new hitch).
- [ ] `layerCount`/`lodCount` read from the header, not hard-coded.
- [ ] Vertices bit-identical (spot-check a known feature's coords — un-shuffle is exact).
- [ ] init-state builds / loads; graph stats match (nodes/edges/stations).
- [ ] (Optional / v9) Signature: a correctly-signed map loads; a tampered map is rejected.

formap-side cross-check tools (run against any v8 file): `formap --verify-sig <file> <pub>` (signature + all block hashes), `formap --read-v8 <file>` (per-layer feature/vertex totals to compare against your decode).

---

## 6. Gotchas

- **Read `LayerCount`/`LODCount` from the header dynamically.** Hard-coding the count is the exact v7 trap that desynced readers on 12- vs 13-layer files.
- **Read the per-LOD index hashes even if you don't verify** — they're always present in the v8 index; skipping them desyncs the index parse (`+lodCount×32` bytes/entry).
- **No per-block length prefix** in v8 — locate blocks purely by the index `FileOffset`/`CompressedSize`.
- **bbox is derived**, not stored.
- **Vertices are pooled + byte-shuffled per block** — you must un-shuffle and re-distribute by vertexCount; you can't read a feature's vertices inline.
- **Decode stays LZ4** — do not pull in Zstd; it was rejected for v8 specifically to protect your load time (Zstd decode is ~3× slower). Zstd is reserved for a future v9/v10 if ever wanted.
- The format is **final** — implement against this spec + `format-v8.md` with confidence.

---

## 7. Pointers

- [`format-v8.md`](format-v8.md) — authoritative byte spec, the full change log, and the design rationale (why each change, the benchmarks, the decisions).
- Reference reader (C#): `BinaryFormatV8.cs` (`ReadV8` / `DecodeBlock` / `DecodeBlockFiltered` / `ReadLogicLayersV8` / `VerifySignatureV8`), `FeatureCodecV8.cs` (`ReadFeatureStructure` / `SkipFeatureStructure` / `ReadVarint` / `Unshuffle`), `BinaryFormat.cs` (`ReadHeaderV8`), `Signing.cs` (`Verify`).

---

## 8. formap-side change log — init-state build speedup (2026-06-15)

Pure **formap-side** performance work on the init-state build path. **The v8 format is byte-for-byte unchanged** — every map file is identical — so RM's reader port (§2–§4) is unaffected. Recorded here because it touches the reference reader RM mirrors (`ReadLogicLayersV8`).

**Problem.** Building `init-state-pl.bin` at country scale was slow inside a full `formap x.pbf` run (~22 min) — ~3× slower than the same build run standalone via `--init-state-only` (~6 min). Two causes:
1. `ReadLogicLayersV8` only needs the logic layers (Railways/AdminBoundaries/Places/POIs/Platforms/Coastlines), but it called `DecodeBlock`, which fully materialized **every** LOD0 layer of every tile — including the heavy Buildings/Forests/Highways/Water geometry — then discarded all but the logic layers.
2. In a full run, `InitStateBuilder` re-reads the just-written `.bin` while the process still holds the entire conversion state (tile grid + feature lists + way cache, GBs) in RAM → memory/GC pressure.

**Fix — read path (`FeatureCodecV8` / `BinaryFormatV8`).**
- `FeatureCodecV8.SkipFeatureStructure` (new) — advances the reader past one feature's struct record byte-for-byte, in lockstep with `ReadFeatureStructure`, **without allocating anything**; returns only `vertexCount`.
- `BinaryFormatV8.DecodeBlockFiltered(body, table, wanted)` (new) — materializes only the wanted layers. Non-wanted features are skipped in the struct, and their vertices are skipped in the block pool (the pool is byte-shuffled as a whole, so the whole pool is still un-shuffled — but **no `MeshGeometry`/`Vector2`/index/metadata objects are built** for non-wanted layers). A block with no wanted layer returns before the pool is touched.
- `ReadLogicLayersV8` now also reads each tile's LOD0 `LayerMask` from the index and **skips entire blocks** where no wanted layer is present (`(mask & wantedMask) == 0`) — no LZ4 decompress, no decode at all (87 such blocks in Poland).

**Fix — memory (`OsmConverter` / `Program`).**
- `OsmConverter.ReleaseConversionState()` (new) clears the feature lists, the (otherwise never-freed) way cache, and the work buffers, then forces a GC. `Program` calls it right before the in-process InitState phase — which re-reads the written `.bin`, so the in-memory state was dead weight.

**Output is identical.** The read-back data path is unchanged — it had to be: `TileGrid.AddFeature` adds each feature to **every** tile its bbox intersects (no clipping), so multi-tile features are intentionally duplicated, and the graph stats depend on that duplication. Only wasted work was removed.

**Verification (full Poland).**
- `formap --verify-logic <v8>` (new regression command) decodes every LOD0 block both via `DecodeBlock` and `DecodeBlockFiltered`, asserts the logic-layer features are bit-identical, **and** recomputes each block's `LayerMask` from the layers actually present and asserts it equals the stored index mask (proving the whole-block skip can never drop a wanted layer). On two independently-built v8 files: **4,892 blocks, 274,529 logic features, 0 mismatches, 0 mask mismatches.**
- Standalone `--init-state-only poland-v8.bin` reproduces the exact graph stats: **570,968 nodes / 1,149,864 edges / 49,532 junctions / 3,381 stations / 10,628 platforms**; logic feature counts Railways 130,337 · POIs 61,009 · Places 48,518 · Platforms 23,568 · AdminBoundaries 11,097.

**Result (timing).** In-run InitState build: **~22 min → ~6.4 min** (384.9 s on the full run), now on par with the standalone build (~361.5 s) — the in-run memory-pressure penalty is gone. The conversion phase itself is untouched.

> The conversion's feature *ordering* is non-deterministic (parallel parse), so a *fresh* full conversion yields graph stats within a few units of the above (observed 570,969 nodes / 1,149,862 edges). This run-to-run jitter at tolerance-based merge boundaries pre-dates and is independent of this change — the deterministic logic feature counts are exact, and reading a *fixed* file reproduces its stats exactly.

**RM relevance.** None required — format unchanged, reader port unaffected. *If* RM ever uses the §2 fallback (rebuild the graph from the `.bin` instead of loading `init-state-pl.bin`), it can mirror the optimized `ReadLogicLayersV8`: read the LOD0 `LayerMask`, skip blocks where `(mask & wantedMask) == 0`, and skip — rather than materialize — non-logic layers. Purely a speed/memory win.
