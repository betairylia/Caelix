# Caelix

**This repository is in an early research and experimental stage. Expect massive API changes.**

I'm sorry that the current docs are very sloppy. Later (not anytime soon, tho) I will write some proper docs by myself.

<img width="480" height="560" align="center" alt="silly-bird-loop" src="https://github.com/user-attachments/assets/4e62968c-d537-4bd5-be48-49b1a0201753" />

---

Caelix is an experimental voxel engine package for Unity URP.

This package contains rendering, authoring, and world orchestration. It uses Caelix Core for voxel data and Caelix Physics for simulation.

## Roadmap

- v0.2: Optimize memory usage; Improved rendering & GI; Playable LAN multiplayer
- v0.3: Proper physics engine with at least contact merging & manifold creation; Infinite world streaming

## Related Repositories

- [Caelix-Core](https://github.com/betairylia/Caelix-Core) — Basic voxel data layouts + I/O.
- [Caelix-physics](https://github.com/betairylia/Caelix-physics) — Voxel fork of Unity Physics.
- [Caelix-Renderer](https://github.com/betairylia/Caelix-Renderer) — Caelix's Vulkan native rendering plugin.
- [Caelix](https://github.com/betairylia/Caelix) — Custom voxel rendering & physics engine backend.
- [Titania](https://github.com/betairylia/Titania) — A voxel sandbox project built on Caelix, demonstrates Caelix usage.

## License

Caelix is licensed under the GNU General Public License, version 3 (see `LICENSE`),
with the following additional permission.

### Additional permission under GNU GPL version 3 section 7

If you modify this Program, or any covered work, by linking or combining it
with any of the following, the licensors of this Program grant you additional
permission to convey the resulting work:

  (a) the Unity Engine and Unity Editor, and packages distributed by Unity
      Technologies under the Unity Companion License or the Unity Package
      Distribution License, including the Caelix Physics package
      (`ink.irylia.caelix.physics`), a derivative of Unity Physics;
  (b) the Caelix native renderer binaries (`caelix_renderer`, `caelix_unity`) and
      the NVIDIA Real-Time Denoisers and NVIDIA DLSS / NGX SDK components they
      contain, or modified versions of those libraries.

The software listed above is not covered by this License. It remains under
its own license terms, which you must comply with. Corresponding Source for a
non-source form of such a combination need not include the source code of the
software listed above.

The native Vulkan renderer needs `caelix_unity.dll`, `caelix_renderer.dll`,
`nvngx_dlss.dll` and `nvngx_dlssd.dll` in `Runtime/Plugins/x86_64`. They are not part
of this repository: take them from a
[Caelix-Renderer](https://github.com/betairylia/Caelix-RenderingPlugin) release, which
carries their third-party notices.
