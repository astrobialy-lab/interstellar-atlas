# Gaia Star Rendering — Unity VR

This document describes the Gaia DR3 star-rendering, LOD, streaming, and HDR/glare pipeline used by the Unity VR space-simulation project.

The rendering approach is independently implemented in Unity/URP and is inspired by:

> S. Schneegans, A. Kreskowski, and A. Gerndt, “Smaller than Pixels: Rendering Millions of Stars in Real-Time,” *Eurographics 2025 — Short Papers*.

The project follows the paper's central idea of treating stars as sub-pixel sources and rasterizing their contributions on the GPU. It uses its own Gaia data preparation, octree/MS LOD system, binary formats, streaming, XR integration, and RenderGraph pipeline. It is not the CosmoScout VR implementation.

---

## 1. System overview

The current runtime has three closely connected parts:

1. **MS LOD and streaming** — selects and loads the star nodes needed by the current view.
2. **Live star rasterization** — renders selected stars directly from GPU star buffers.
3. **HDR composition and glare** — reconstructs the star image, generates glare, and composites the result with the camera image.

The runtime flow is:

```text
Gaia DR3
    ↓
offline preprocessing
    ↓
HEALPix + distance-shell index
    ↓
MS LOD octree + particle files
    ↓
GaiaMSLODLoader
    ├── frustum culling
    ├── solid-angle LOD selection
    ├── asynchronous loading
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
       glare pyramid
            ↓
  GaiaStarFinalComposite.shader
            ↓
       final camera image
```

Stars are not represented by individual Unity GameObjects, meshes, or billboard objects.

---

## 2. Gaia data and preprocessing

The project uses **Gaia Data Release 3 (DR3)** as a local offline stellar catalogue. Unity does not download Gaia data during runtime.

The original catalogue organization uses:

- HEALPix level 6
- Radial-distance shells

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
Data/Gaia/lod/metadata.bin
Data/Gaia/lod/particles/particles_XXXXXXXX.bin
```

`index.bin` describes the original Gaia chunk organization. `metadata.bin` and `particles/` are the active MS LOD representation used by `GaiaMSLODLoader.cs`. The old `GaiaStarLoader.cs` is no longer part of the active runtime architecture.

### Spatial bounds

The processed star volume is configured to fit the project's dust bounding box, rather than using an independent radial-distance cutoff. In Unity coordinates, the dust box is centered on the Sun and uses half-extents of approximately:

```text
X: ±1010 pc
Y:  ±410 pc
Z: ±1010 pc
```

The galactic plane maps to Unity's XZ plane, with Y up. Keep the Gaia preprocessing bounds aligned with the dust-volume bounds when either dataset's extent or coordinate mapping changes. The exact selected stars depend on the current preprocessing configuration.

---

## 3. MS LOD and streaming

### `GaiaMSLODLoader.cs`

This is the active runtime LOD and star-data loader.

The processed catalogue is represented as spatially bounded octree nodes. Runtime selection is based primarily on **apparent angular size / solid angle**, rather than distance alone.

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

The loader requests more spatial detail when a region occupies more of the viewer's field of view and uses coarser nodes when it is visually small. The selection also uses preload and fade ranges around the main LOD threshold.

### Streaming

After LOD selection, the loader compares the desired node set with currently resident nodes:

```text
Desired nodes
    ├── already resident → keep
    ├── missing           → load asynchronously
    └── no longer needed  → unload
```

Particle files are read asynchronously and uploaded to `GraphicsBuffer` objects. `maxConcurrentLoads` limits how many node loads may be active at once; the effective value is set by the current runtime configuration.

Only the selected nodes need to be resident in CPU/GPU memory. The complete local catalogue and processed files remain on disk.

---

## 4. Live star rendering

### `GaiaStarRasterizer.cs`

Owns the screen-sized GPU accumulation buffers and provides loaded star buffers to the RenderGraph renderer.

### `GaiaStarRasterizer.compute`

The compute shader processes approximately one star per GPU thread:

```text
star
  ↓
project to screen
  ↓
find sub-pixel position
  ↓
calculate brightness contribution
  ↓
distribute to 2×2 neighbouring pixels
  ↓
atomic fixed-point accumulation
```

The 2×2 distribution reduces pixel popping and gives smoother star motion when stars move across pixel boundaries.

The accumulation uses integer `uint` buffers with a fixed-point scale, allowing many star contributions to be combined with atomic operations.

The runtime GPU star record is 20 bytes:

```text
float3 position
float  magnitude
float  bpRp
```

The current colour model derives an approximate star colour from BP-RP.

---

## 5. HDR composition and glare

### `GaiaStarComposite.shader`

Reconstructs the live star accumulation into an HDR star image:

```text
live star accumulation
          ↓
        StarHDR
```

### Glare pipeline

The HDR star image is processed through a multi-resolution glare pyramid. The current pyramid has five progressively smaller levels, reaching approximately 1/32 of the original resolution. Each level is downsampled and blurred with separable horizontal/vertical Gaussian passes.

### `GaiaStarFinalComposite.shader`

The final stage combines:

```text
camera image
+ sharp StarHDR
+ multi-resolution glare
```

The result is written back as the final camera image.

---

## 6. XR / VR

The rendering path is designed for XR and supports mono and stereo cameras.

For stereo rendering, the live compute rasterizer receives independent left/right view-projection matrices. Each eye's stars are projected using that eye's view, preserving the correct stereo view.

---

## 7. RenderGraph structure

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

---

## 8. Memory model

The system separates catalogue storage, resident LOD data, and screen-space rendering:

```text
Full Gaia catalogue / processed files
              ↓
        local files on disk
              ↓
        selected LOD nodes
              ↓
      CPU / GPU resident buffers
              ↓
    screen-sized accumulation
              ↓
       HDR star image + glare
```

This lets the runtime use a large local stellar catalogue without loading the entire catalogue into CPU/GPU memory or continuously representing stars as individual scene objects. Runtime memory usage depends on selected nodes, streaming concurrency, GPU buffer sizes, and screen-space render targets.

---

## 9. Main runtime classes

| Class | Responsibility |
|---|---|
| `GaiaIndexReader` | Reads the original Gaia HEALPix/shell index. |
| `GaiaMSLODLoader` | Active octree LOD selection, streaming, and GPU star buffers. |
| `GaiaStarBufferProvider` | Interface between star-data providers and rendering. |
| `GaiaStarRasterizer` | Owns live accumulation buffers and renderer settings. |
| `GaiaStarRenderFeature` | URP/RenderGraph integration for the star-rendering pipeline. |
| `GaiaStarFilter` | Optional filtering of stars by supported stellar properties. |
| `GaiaClusterFilter` | Optional filtering of cluster data. |

The old `GaiaStarLoader.cs` is not part of the active runtime architecture.

---

## 10. Current status

The current architecture includes the following major components:

```text
[done] Gaia DR3 offline data
[done] HEALPix + distance-shell organization
[done] MS LOD octree
[done] Frustum culling
[done] Solid-angle LOD selection
[done] Asynchronous node streaming
[done] GPU star buffers
[done] Compute sub-pixel rasterization
[done] Fixed-point accumulation
[done] HDR star reconstruction
[done] Five-level glare pipeline
[done] XR-aware per-eye rendering path
[done] Final camera composite
```

The main remaining work is refinement and calibration rather than replacing the basic architecture. Validate this checklist against the current Unity project before using it as a release checklist.

---

## 11. Future work

Potential next improvements include:

- stricter resident-memory budgets and eviction policies;
- photometric calibration of Gaia magnitude to displayed luminance;
- improved stellar colour/temperature mapping;
- tighter integration with exposure and tone mapping;
- dust extinction and scattering;
- further XR/mobile GPU profiling and optimization.

---

## 12. References

### Rendering reference

Schneegans, S., Kreskowski, A., & Gerndt, A. (2025). **“Smaller than Pixels: Rendering Millions of Stars in Real-Time.”** *Eurographics 2025 — Short Papers*.

- DOI: <https://doi.org/10.2312/egs.20251029>
- DLR: <https://elib.dlr.de/215930/>
- Eurographics Digital Library: <https://diglib.eg.org/items/fc13d662-0f6a-4a23-9ae2-d7f1ec1afc78>

### Reference software

**CosmoScout VR** — open-source scientific visualization software associated with the paper: <https://github.com/cosmoscout/cosmoscout-vr>.

### Gaia data

**Gaia Data Release 3 (DR3)**

- Gaia Archive: <https://gea.esac.esa.int/archive/>
- Gaia DR3 documentation: <https://gea.esac.esa.int/archive/documentation/GDR3/>
- Gaia credit/citation instructions: <https://gea.esac.esa.int/archive/documentation/GDR3/Miscellaneous/sec_credit_and_citation_instructions/>

The project should retain the official Gaia acknowledgement and citation requirements when distributing or publishing work based on Gaia DR3.

---

## 13. Attribution

This is an independent Unity/URP implementation inspired by the cited Eurographics paper. It is not an official CosmoScout VR implementation.

The stellar data originate from the **ESA Gaia mission** and the **Gaia Data Processing and Analysis Consortium (DPAC)**. Official Gaia acknowledgement and citation requirements should be retained when publishing or distributing results derived from Gaia DR3.
