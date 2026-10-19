# caelix-benchmarks

Performance comparison of voxel ray-tracing structures on one machine (RTX 5080): Caelix (hardware ray query over
brick AABBs), the VoxelRT methods (Tree64, ESVO, StdBVH, XBrickMap, MultiDDA, PlainDDA, three distance fields),
NanoVDB (PNanoVDB HDDA inside the VoxelRT harness) and NAADF. Produced 2026-10-18/19.

**Read `report.md`.** It has the method summaries, the protocol, the results with figures and the limitations.

## Layout

| Path | Content |
| --- | --- |
| `report.md` | the report (markdown; figures are PNGs in `figures/`) |
| `notes/` | research notes per harness (`caelix.md`, `voxelrt.md`, `naadf.md`, `nanovdb.md`), `literature.md` (background with DOIs), `protocol.md`, `results-schema.md` |
| `scenes/` | converted scenes (`.dat` for VoxelRT, `.cvox` for NAADF, FractalCave `.cxw`), `scenes/README.md` with the conversion table, coordinate offsets and material caveats |
| `scripts/` | `cxw_to_voxelrt_dat.py`, `cvox_convert.py`, `fractalcave.py`, `run_caelix.py`, `run_voxelrt.py`, `run_naadf.py`, `aggregate.py` (+ `test_aggregate.py`), `gpu_lock.py`, `gpu_clock_log.py` |
| `results/<harness>/` | one JSON (+ PNG + log) per run; `results/reference-views/` the Caelix views of every pose; `results/experiments/` the coarse-traversal control and the clock log; `results/*/smoke/` the implementers' smoke tests (not used in the report) |
| `tables/` | generated summary tables (`summary_<scene>_<mode>.md`, `memory.md`, `dynamic.md`, `all_runs.csv`) |
| `figures/` | generated charts and screenshot montages |
| `third_party/nanovdb/` | NanoVDB headers from OpenVDB v13.1.0 (Apache-2.0), with SHA256 list in `notes/nanovdb.md` |
| `logs/` | console logs of the measurement loops |

## Code branches

`bench/caelix-benchmarks-20261018` in `F:\RP_Games\Caelix-family\Caelix-Renderer`, `D:\RP_CG\VoxelRT` and
`F:\RP_Games\Caelix-family\Research\NAADF`. Nothing was merged. Each branch contains the harness's `--bench` mode and
the bug fixes listed in report section 3.5.

## Rerun

1. Build: Caelix-Renderer (`cmake --build --preset release`, `cargo build --release` in `host-rs`), VoxelRT
   (`build-release/`, see `notes/voxelrt.md` 1.4 and the implementer's `vbuild.bat` notes in report 3.5), NAADF
   (`dotnet build -c Release NAADF/NAADF.csproj`).
2. Scenes: `scenes/README.md` has the commands; the `.cxw` sources stay untouched.
3. `python scripts/run_caelix.py`, `python scripts/run_voxelrt.py`, `python scripts/run_naadf.py` (each holds
   `.gpu-lock`; run them one after another or let the lock serialize them). Use `--dry-run` to list the cases.
4. `cd scripts && uv run --with numpy --with matplotlib python aggregate.py` regenerates `tables/` and `figures/`.
