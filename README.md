# formap

**formap** converts OpenStreetMap data (`.osm.pbf`) into a compact, tiled, multi‑LOD binary mesh format optimized for real‑time rendering in games.

It parses raw OSM geometry, triangulates polygons (including concave shapes and multipolygons with holes), projects coordinates to a local metric system, partitions features into a spatial tile grid with multiple levels of detail, and writes an LZ4‑compressed binary file with a seekable tile index for fast on‑demand loading.

> Originally built to power the in‑game map in commercial rail‑simulation titles. Released publicly as a portfolio / reference project.

## Features

- **OSM parsing** (via [OsmSharp](https://github.com/OsmSharp/core)) — nodes, ways, and relations (multipolygons, railway route relations).
- **13 feature layers** — railways, highways, buildings, water, waterways, forests, industrial, military, platforms, POIs (stations / halts / signals), admin boundaries, places, coastlines.
- **Polygon triangulation** — a built-in ear-clipping triangulator for simple polygons, with [LibTessDotNet](https://github.com/speps/LibTessDotNet) for multipolygons with holes (ear-clipping as fallback).
- **Spatial tiling** — 10 km × 10 km grid with a Cantor‑paired tile id for direct lookup.
- **6 levels of detail (LOD)** — progressive per‑zoom filtering of features and road classes.
- **LZ4‑compressed per‑tile blocks** with a seekable index — load only the tiles you need.
- **Local metric projection** — equirectangular around the input centroid (accurate for regional extents).
- **Railway graph / init‑state** (optional) — pathfinding graph, stations, platforms, signals and block sections, pre‑built for fast load.

## Build

Requires the **.NET 8 SDK**.

```bash
dotnet build -c Release
```

## Usage

```bash
dotnet run -c Release -- <input.osm.pbf> [output.bin] [--format v8|v7] [--country PL] [--no-init-state]
```

Example:

```bash
dotnet run -c Release -- poland.osm.pbf poland.bin
```

- `--format v8` (default) — tiled, multi‑LOD binary mesh (`FORMAP04`); lossless re‑encoding of v7, ~44% smaller.
- `--format v7` — legacy format (`FORMAP03`).
- `--country <code>` — ISO 3166‑1 alpha‑2 code for the init‑state build (default `PL`).
- `--no-init-state` — skip building the railway pathfinding / init‑state sidecar.

> **Tip:** for a focused area, cut a bounding box first with [`osmium extract`](https://osmcode.org/osmium-tool/) before running formap — the projection origin is the centroid of the input, so a smaller input yields more local, precise coordinates.

## Output format

A binary container (`FORMAP04` / v8): a 128‑byte header (carrying `LayerCount` / `LODCount` / `compressionType`) → a global metadata string table → LZ4‑compressed tile blocks (6 LOD levels each; per‑feature structure + a pooled, structure‑of‑arrays + byte‑shuffled vertex section) → a seekable tile index. It is a **lossless** re‑encoding of v7 (~44% smaller); per‑feature bounding boxes are derived from vertices on read. The authoritative layout lives in [`BinaryFormat.cs`](BinaryFormat.cs), [`FeatureCodecV8.cs`](FeatureCodecV8.cs), [`BinaryFormatV8.cs`](BinaryFormatV8.cs) and [`MeshGeometry.cs`](MeshGeometry.cs); the full spec + change log is in [`docs/format-v8.md`](docs/format-v8.md). The legacy v7 (`FORMAP03`) layout remains available via `--format v7`.

## Data & attribution

Input data comes from **OpenStreetMap**. OSM map data is © OpenStreetMap contributors, licensed under the [Open Database License (ODbL)](https://www.openstreetmap.org/copyright). Any map data you **generate and distribute** with formap is subject to ODbL (attribution, and share‑alike where applicable). That obligation falls on whoever distributes the generated data and is independent of formap's own license.

## Dependencies

| Library | License |
|---|---|
| [OsmSharp](https://github.com/OsmSharp/core) | MIT |
| [LibTessDotNet](https://github.com/speps/LibTessDotNet) | MIT (tessellator core under the SGI Free Software License B) |
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | MIT |

See [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) for full notices.

## License

Licensed under the **MIT License** — see [`LICENSE`](LICENSE).

© 2026 Mikołaj Tomczak
