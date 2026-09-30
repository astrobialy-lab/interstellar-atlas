# Gaia Star Rendering — Unity VR

This document describes the current Gaia DR3 star-rendering, LOD, streaming, and far-field caching system used by the Unity VR space-simulation project.

The rendering approach is independently implemented in Unity/URP and is inspired by:

> S. Schneegans, A. Kreskowski, and A. Gerndt, “Smaller than Pixels: Rendering Millions of Stars in Real-Time,” Eurographics 2025.

The project follows the paper's central idea of treating distant stars as sub-pixel sources and rasterizing them on the GPU, but uses its own Gaia data preparation, octree/MS LOD system, binary format, streaming system, XR integration, and RenderGraph pipeline.

---

## 1. System overview

The current runtime has four closely connected parts:

1. **MS LOD + streaming** — selects and loads the star nodes needed by the current view.
2. **Live star rasterization** — renders nearby stars directly from GPU star buffers.
3. **Far-star cache** — stores very distant stars as a dynamically generated 360° panorama around the player.
4. **HDR glare** — combines live and cached stars, then generates the final star/glare image.

The runtime flow is:

```text
Gaia DR3
    ↓
offline preprocessing
    ↓
HEALPix + distance-shell data
    ↓
MS LOD octree + particle files
    ↓
GaiaMSLODLoader
    ├── frustum culling
    ├── solid-angle LOD
    ├── async loading
    └── GPU GraphicsBuffers
            ↓
      GaiaStarRasterizer
            ↓
   GaiaStarRasterizer.compute
            ↓
   fixed-point star accumulation
            ↓
   GaiaStarComposite.shader
            ↓
        Star HDR
            ↓
   live stars + far cache
            ↓
      glare pyramid
            ↓
GaiaStarFinalComposite.shader
            ↓
      final VR image
```

Stars are not represented by individual Unity GameObjects, meshes, or billboard objects.

---

## 2. Gaia data and preprocessing

The project uses **Gaia DR3** as a local offline stellar catalogue. Unity does not download Gaia data during runtime.

The original catalogue organization uses:

- HEALPix level 6
- radial distance shells

```text
0–100 pc
100–250 pc
250–500 pc
500–1000 pc
1000–2000 pc
2000–5000 pc
5000–10000 pc
10000–20000 pc
20000–40000 pc
40000+ pc
```

The original HEALPix/shell structure is useful for data locality and preprocessing. For runtime rendering, the stars are additionally converted into an **octree-based multi-scale LOD representation**.

The active runtime files are:

```text
Data/Gaia/index.bin
Data/Gaia/lod_test/metadata.bin
Data/Gaia/lod_test/particles/particles_XXXXXXXX.bin
```

`index.bin` describes the original Gaia chunk organization. `metadata.bin` and `particles/` are the active MS LOD representation used by `GaiaMSLODLoader.cs`.

The old `GaiaStarLoader.cs` is no longer part of the active runtime architecture.

---

## 3. MS LOD and streaming

### `GaiaMSLODLoader.cs`

This is the active runtime LOD and star-data loader.

The processed catalogue is represented as spatially bounded octree nodes. Runtime selection is based primarily on **apparent angular/solid angle**, not just distance.

Conceptually:

```text
camera
  ↓
frustum culling
  ↓
node apparent-size test
  ├── small → keep current node
  └── large → recurse into children
```

The loader therefore increases spatial detail when a region occupies more of the viewer's field of view and uses coarser nodes when it is visually small.

The current selection also uses preload and fade ranges around the main LOD threshold.

### Streaming

After LOD selection, the loader compares the desired node set with currently resident nodes:

```text
Desired nodes
    ├── already resident → keep
    ├── missing          → async load
    └── no longer needed → unload
```

Particle files are read asynchronously and uploaded to `GraphicsBuffer`s. `maxConcurrentLoads` limits how many node loads may be active at once; the current working setting is **16**.

Only the selected nodes need to be resident in CPU/GPU memory. The complete Gaia catalogue remains on disk.

---

## 4. Live star rendering

### `GaiaStarRasterizer.cs`

Owns the screen-sized GPU accumulation buffers and provides the loaded star buffers to the RenderGraph renderer.

### `GaiaStarRasterizer.compute`

The compute shader processes approximately one star per GPU thread:

```text
star
 ↓
project to screen
 ↓
find sub-pixel position
 ↓
calculate brightness
 ↓
distribute to 2×2 neighbouring pixels
 ↓
atomic fixed-point accumulation
```

The 2×2 distribution reduces pixel popping and gives smoother star motion when stars move across pixel boundaries.

The accumulation uses integer `uint` buffers with a fixed-point scale, allowing many star contributions to be combined with atomic operations.

The runtime GPU star record is only 20 bytes:

```text
float3 position
float  magnitude
float  bpRp
```

The current colour model derives an approximate star colour from BP-RP.

---

## 5. Far-star cache

Very distant stars are not kept as continuously rendered individual stars. Instead, the project uses a **dynamic far-field image cache**.

### Representation

The cache is a single **2:1 equirectangular HDR panorama**:

```text
2048 × 1024
360° horizontal
180° vertical
```

It represents the entire sky around a cache position. It is not a cubemap, so there are no separate cube faces to update.

### Current distance split

The current configuration is:

```text
≤ 10,000 Unity units  → live MS LOD rendering
> 10,000 Unity units  → far panorama cache
```

The live rasterizer uses the same cache position and distance to reject stars outside the live range, preventing duplicate rendering.

### Cache generation

`GaiaFarStarCache.cs` selects octree nodes that can contribute stars beyond the far-distance boundary, loads those nodes, and rasterizes their far stars into accumulation buffers. The accumulated data are then reconstructed into the equirectangular HDR panorama.

The published cache is kept intact while a new one is being generated. When generation finishes, the new panorama is swapped in.

Thus the cache is **runtime-generated and local**, not a set of prebaked images stored for every possible location.

### Cache refresh

The cache is regenerated when the player has moved enough that the angular error of the existing far view exceeds the configured threshold.

Current settings:

```text
Far Cache Distance      = 10,000
Cache Angular Error     = 1°
```

The corresponding movement threshold is approximately:

```text
10,000 × tan(1°) ≈ 175 Unity units
```

So the current cache is normally regenerated after roughly 175 units of player translation. Rotation alone does not require regeneration because the panorama already covers the full 360° sky.

### VR sampling

The panorama itself is shared by both eyes. During final rendering, each eye reconstructs its own world-space viewing ray and samples the same panorama. This preserves the world-space alignment of the cache while keeping the stereo view dependent on the actual left/right eye cameras.

---

## 6. HDR composition and glare

### `GaiaStarComposite.shader`

Reconstructs the live star accumulation into HDR and, when valid, adds the far panorama sample in the same pass.

The result is a single star HDR signal:

```text
live nearby stars
       +
far cached stars
       ↓
    StarHDR
```

### Glare pipeline

The HDR star image is then processed through a multi-resolution glare pyramid. The current pyramid has five progressively smaller levels, reaching approximately 1/32 of the original resolution.

Each level is downsampled and blurred with separable horizontal/vertical Gaussian passes.

### `GaiaStarFinalComposite.shader`

The final stage combines:

```text
camera image
+ sharp StarHDR
+ multi-resolution glare
```

The result is written back as the final camera image.

---

## 7. XR / VR

The rendering path is designed for XR and supports mono and stereo cameras.

For stereo rendering, the live compute rasterizer receives independent left/right view-projection matrices. The far-cache sampling path likewise reconstructs an independent world ray for each eye while using the same world-space panorama.

This is important because a single cached image is shared spatial information; it should not be interpreted using one eye's camera transform for both eyes.

---

## 8. RenderGraph structure

`GaiaStarRenderFeature.cs` connects the GPU stages through Unity URP RenderGraph:

```text
loaded star GraphicsBuffers
        ↓
compute accumulation
        ↓
star HDR reconstruction
        ↓
glare prefilter / pyramid
        ↓
final composite
```

Intermediate HDR textures are tracked as RenderGraph resources. Resources shared between separate passes use the explicit RenderGraph global-texture mechanism rather than relying on unrestricted global command-buffer state.

The far panorama is an externally owned runtime `RenderTexture` that is imported into RenderGraph when it is valid.

---

## 9. Memory model

The system deliberately separates **catalogue storage**, **resident LOD data**, and **screen-space rendering**:

```text
Full Gaia catalogue
        ↓
local binary files on disk
        ↓
selected LOD nodes
        ↓
CPU / GPU resident buffers
        ↓
screen-sized accumulation
```

For the far field, the design adds another layer:

```text
very distant stars
        ↓
dynamic equirectangular HDR cache
        ↓
shared 360° panorama
```

This allows the project to retain the visual presence of a much larger stellar catalogue without continuously rendering every distant star as an individual source.

---

## 10. Main runtime classes

| Class | Responsibility |
|---|---|
| `GaiaIndexReader` | Reads the original Gaia HEALPix/shell index. |
| `GaiaMSLODLoader` | Active octree LOD selection, streaming, and GPU star buffers. |
| `GaiaStarBufferProvider` | Interface between star-data providers and rendering. |
| `GaiaStarRasterizer` | Owns live accumulation buffers and renderer settings. |
| `GaiaStarRenderFeature` | URP/RenderGraph integration for the complete star pipeline. |
| `GaiaFarStarCache` | Builds, refreshes, and publishes the far-field panorama. |

---

## 11. Current status

The current architecture has the following major pieces working:

```text
[done] Gaia DR3 offline data
[done] HEALPix + distance-shell organization
[done] MS LOD octree
[done] Frustum culling
[done] Solid-angle LOD selection
[done] Async node streaming
[done] GPU star buffers
[done] Compute sub-pixel rasterization
[done] Fixed-point accumulation
[done] HDR star reconstruction
[done] Dynamic far-star panorama cache
[done] Live/far distance separation
[done] Five-level glare pipeline
[done] XR-aware per-eye rendering path
[done] Final camera composite
```

The main remaining work is refinement and calibration rather than replacing the basic architecture.

---

## 12. Future work

Potential next improvements include:

- stricter resident-memory budgets and eviction policies
- photometric calibration of Gaia magnitude to displayed luminance
- improved stellar colour/temperature mapping
- tighter integration with exposure and tone mapping
- better far-cache generation performance and refresh scheduling
- dust extinction and scattering
- further XR/mobile GPU profiling and optimization

---

## 13. References

### Rendering reference

Schneegans, S., Kreskowski, A., & Gerndt, A. (2025).
**Smaller than Pixels: Rendering Millions of Stars in Real-Time.**
Eurographics 2025 — Short Papers.

DOI: https://doi.org/10.2312/egs.20251029

DLR: https://elib.dlr.de/215930/

### Reference software

**CosmoScout VR** — open-source scientific visualization software associated with the paper:
https://github.com/cosmoscout/cosmoscout-vr

### Gaia data

**Gaia Data Release 3 (DR3)**

Gaia Archive: https://gea.esac.esa.int/archive/

Gaia DR3 documentation: https://gea.esac.esa.int/archive/documentation/GDR3/

Gaia credit/citation instructions:
https://gea.esac.esa.int/archive/documentation/GDR3/Miscellaneous/sec_credit_and_citation_instructions/

The project should retain the official Gaia acknowledgement and citation requirements when distributing or publishing work based on Gaia DR3.

---

## 14. Attribution

This is an independent Unity/URP implementation inspired by the cited Eurographics paper. It is not an official CosmoScout VR implementation.

The stellar data originate from the **ESA Gaia mission** and the **Gaia Data Processing and Analysis Consortium (DPAC)**. Official Gaia acknowledgement and citation requirements should be retained when publishing or distributing results derived from Gaia DR3.
