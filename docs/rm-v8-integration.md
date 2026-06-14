# RM ← formap v8 — integration & deployment guide

**For:** the RM (Unity / C#) developer.
**Goal:** make RM read the new **v8 (`FORMAP04`)** map format and deploy the new, ~half-size signed map.
**Status:** the v8 format is **FINAL**. formap produces it by default. RM still reads v7 — this is the work to switch.
**Byte-level spec + rationale:** [`format-v8.md`](format-v8.md) is the authoritative source of truth; this doc is the *how-to* for RM.

---

## 0. TL;DR

- v8 is a **lossless** re-encoding of v7 — same data, same LOD/tiling/duplication, ~**44% smaller** on disk (Poland 22.5 GB → 11.2 GB), and **fast LZ4 decode** (no new decompressor).
- RM's job: **a v8 read path** + (recommended) **Ed25519 signature verification**, then **swap the map file**.
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

**init-state:** if your gameplay graph is rebuilt from the `.bin` (not from `init-state-pl.bin`), mirror `ReadLogicLayersV8` — it reads only LOD0 + the logic layers (Railways/AdminBoundaries/Places/POIs/Platforms/Coastlines). In practice you load the precomputed `init-state-pl.bin` (regenerated by formap), so this path is the fallback.

---

## 3. Signature verification (recommended)

Provenance + integrity: only maps you signed load; tampering is detected. Cheap and one-time (the per-block hash check is ~0.18 ms/tile and rides your existing per-frame streaming).

1. Add a managed Ed25519 to RM — **BouncyCastle.Cryptography** works in Unity (IL2CPP); port `Signing.Verify`.
2. **Embed the public key** (32 bytes, from `formap --gen-key`) in RM as a constant `byte[]`.
3. At map open, if `signatureLength == 64`: read the trailing 64 bytes, `Ed25519.Verify(pubKey, indexBytes, sig)` over the index byte region. **Refuse to load on failure.**
4. While streaming each block: `SHA256(compressedBytes)` and compare to the index's per-LOD hash. **Refuse the tile on mismatch.**

(Verifying only the index signature already gives provenance at open; the per-block hashes add content-integrity during streaming. Both are in the format.)

---

## 4. Deployment steps

1. **Generate the production keypair (once):** `formap --gen-key <path>` → `<path>.priv` (keep **secret, never in any repo**) + `<path>.pub` (embed in RM/SD). The `testkey.*` in `D:/Gry/fmstat/` is a throwaway test key — do not ship it.
2. **Regenerate the map signed:** `formap poland-260613.osm.pbf poland-v8.bin --sign-key <path>.priv` → produces signed `poland-v8.bin` + `init-state-pl.bin` (~42 min map + ~7 min init-state). ⚠ The current `D:/Gry/formap/poland-v8.bin` is an **unsigned, pre-signing-format** build — regenerate it with the current formap.
3. **Drop into RM:** replace `Assets/StreamingAssets/Maps/Poland/poland-v7.bin` with `poland-v8.bin` and the new `init-state-pl.bin`. Either rename to `poland-v8.bin` + update `MapLoader.mapFileName`, or dispatch by magic and keep the path.
4. **Build + smoke-test** (see §5).

---

## 5. Verification checklist (RM side)

- [ ] Map loads; magic-dispatch picks the v8 path for `FORMAP04`, v7 path still works for old files.
- [ ] Renders identically to v7 (all layers, all LODs; LOD switching has no new hitch).
- [ ] `layerCount`/`lodCount` read from the header, not hard-coded.
- [ ] Vertices bit-identical (spot-check a known feature's coords — un-shuffle is exact).
- [ ] init-state builds / loads; graph stats match (nodes/edges/stations).
- [ ] Signature: a correctly-signed map loads; a tampered map is rejected.

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
- Reference reader (C#): `BinaryFormatV8.cs` (`ReadV8` / `DecodeBlock` / `ReadLogicLayersV8` / `VerifySignatureV8`), `FeatureCodecV8.cs` (`ReadFeatureStructure` / `ReadVarint` / `Unshuffle`), `BinaryFormat.cs` (`ReadHeaderV8`), `Signing.cs` (`Verify`).
