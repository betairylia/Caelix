# Caelix

Experimental voxel engine for Unity URP: world simulation, rendering, and authoring.
Uses [Caelix Core](https://github.com/betairylia/Caelix-Core) for storage and
[Caelix Physics](https://github.com/betairylia/Caelix-physics) for physics.

Start with [Get started](Documentation~/manual/get-started.md), or choose a task
in the [documentation](Documentation~/index.md).

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
