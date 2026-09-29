# Gaia Star Rendering — Unity VR

This document describes the current Gaia star-rendering and level-of-detail (LOD) system used in the Unity VR space-simulation project.

The renderer is designed for a very large local Gaia DR3 catalogue and is built around ideas from:

> **S. Schneegans, A. Kreskowski, and A. Gerndt, “Smaller than Pixels: Rendering Millions of Stars in Real-Time,” Eurographics 2025.**

The implementation here is an **independent Unity/URP implementation inspired by the paper**, not a copy of the CosmoScout VR implementation. The project uses the same central idea—treat distant stars as sub-pixel sources, rasterize them in a compute shader, then add a glare/post-processing stage—but adapts the data layout, streaming system, LOD system, colour representation, and RenderGraph integration to this project.

---

## 1. System overview

The current system has two main runtime parts:

1. **LOD + streaming** — decides which parts of the Gaia catalogue are needed and loads them from local binary files.
2. **Rendering** — takes the loaded stars, rasterizes them on the GPU, reconstructs the HDR star image, and produces the glare effect.

The logical flow is:

```text
Gaia DR3
    │
    ▼
Offline preprocessing
    │
    ├── spatial partitioning
    ├── LOD / octree construction
    └── compact binary particle files
    │
    ▼
Runtime LOD selection
    │
    ├── frustum culling
    ├── solid-angle LOD selection
    ├── preload / fade ranges
    └── load / unload decisions
    │
    ▼
GaiaMSLODLoader.cs
    │
    │  asynchronously loads selected nodes
    ▼
Loaded GraphicsBuffers
    │
    ▼
GaiaStarRasterizer.cs
    │
    ▼
GaiaStarRasterizer.compute
    │
    │  one GPU thread per star
    │  project star to screen
    │  distribute sub-pixel contribution over 2×2 pixels
    │  atomic fixed-point accumulation
    ▼
Luminance + colour accumulation buffers
    │
    ▼
GaiaStarComposite.shader
    │
    ▼
HDR star texture
    │
    ├───────────────────────────────┐
    │                               │
    ▼                               ▼
GaiaStarGlare.shader          Sharp star image
    │
    ├── prefilter bright stars
    ├── downsample
    ├── horizontal Gaussian
    └── vertical Gaussian
    │
    ▼
5-level glare pyramid
    │
    ▼
GaiaStarFinalComposite.shader
    │
    ├── original camera image
    ├── sharp stars
    └── weighted glare pyramid
    │
    ▼
Final camera image
```

Stars are **not** rendered as individual GameObjects, meshes, or billboard objects. The catalogue remains data-driven: only the required LOD nodes are resident, and the GPU processes the stars inside those nodes directly.

---

## 2. Why this rendering approach

A conventional point or billboard renderer has two important problems for a huge star catalogue:

1. Very small stars can jump between discrete pixel locations as the camera moves.
2. Rendering large numbers of astronomical sources as geometry creates unnecessary CPU/GPU overhead.

The reference paper addresses this by treating stars as sub-pixel point sources. Each star is projected into screen space and its contribution is distributed over a 2×2 pixel neighbourhood according to its fractional pixel position.

This project follows that general strategy. Accumulation is implemented with integer buffers and a fixed-point scale so that many stars can safely contribute to the same pixel using atomic operations.

---

## 3. Project file structure

The relevant Unity-side code is organized approximately as follows:

```text
Assets/
├── Scripts/
│   └── Gaia/
│       ├── GaiaIndexReader.cs
│       ├── GaiaMSLODLoader.cs
│       ├── GaiaStarBufferProvider.cs
│       ├── GaiaStarRasterizer.cs
│       └── GaiaStarRenderFeature.cs
│
└── Shaders/
    └── Gaia/
        ├── GaiaStarRasterizer.compute
        ├── GaiaStarComposite.shader
        ├── GaiaStarGlare.shader
        └── GaiaStarFinalComposite.shader

Data/
└── Gaia/
    ├── index.bin
    └── lod_test/
        ├── metadata.bin
        └── particles/
            ├── particles_00000000.bin
            ├── particles_00000001.bin
            └── ...
```

The exact directory contents depend on the preprocessing run. The important distinction is:

- `index.bin` belongs to the original HEALPix/distance-shell catalogue organization.
- `lod_test/metadata.bin` and `lod_test/particles/` belong to the current octree-based MS LOD representation used by `GaiaMSLODLoader.cs`.

The old `GaiaStarLoader.cs` is no longer part of the active runtime architecture. `GaiaMSLODLoader.cs` is the current star-data loader used by the renderer.

---

## 4. Gaia data preparation

### 4.1 Gaia source data

The renderer uses Gaia DR3 as the underlying stellar catalogue.

The raw Gaia download is processed offline. Unity does **not** query Gaia over the network while rendering.

The purpose of preprocessing is to turn the very large catalogue into compact binary data structures that can be loaded incrementally during runtime.

### 4.2 Original catalogue partitioning

The source catalogue is spatially organized using:

- **HEALPix level:** 6
- **Distance shells:**

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

This gives angular/spatial locality through HEALPix and radial locality through the distance shells.

### 4.3 Runtime LOD representation

For rendering, the stars are additionally converted into an **octree / multi-scale LOD structure**.

The LOD metadata stores, for each node:

```text
node ID
center
half-size
LOD depth
star count
cumulative star count
child count
child node IDs
```

The corresponding star data are stored in separate binary particle files.

The important idea is that runtime selection does not inspect every Gaia star. It first selects appropriate octree nodes, then only loads the star data for those nodes.

---

## 5. LOD and streaming system

The runtime LOD system is implemented primarily in:

```text
GaiaMSLODLoader.cs
```

with the renderer accessing its loaded GPU data through:

```text
GaiaStarBufferProvider.cs
```

This is the part of the project that determines **which stars should exist in memory right now**.

### 5.1 Octree hierarchy

The processed star catalogue is represented as a hierarchy of spatially bounded nodes.

Conceptually:

```text
                    Root
                 /   / \   \
                /   ...    \
             Child       Child
            /   \         /   \
          ...   ...     ...   ...
```

Each node contains its own stored star records, so a selected parent node can contribute stars while its children are also selected for finer detail.

The hierarchy allows the renderer to use coarse representations when a region is visually small and progressively finer nodes when the same region occupies more of the viewer's field of view.

### 5.2 Frustum culling

Before doing LOD recursion, each node is tested against the camera frustum.

A node whose bounding box lies outside the camera frustum is ignored completely.

Conceptually:

```text
octree node
    ↓
frustum test
    ├── outside → reject
    └── visible → continue LOD test
```

This prevents off-screen catalogue regions from being loaded merely because they are spatially close to the player.

### 5.3 Solid-angle based LOD

The important LOD criterion is **projected angular size**, not catalogue distance alone.

`GaiaMSLODLoader.cs` estimates the apparent solid angle of each node's bounding box as seen from the camera.

Conceptually:

```text
large apparent solid angle
        ↓
node occupies significant visual area
        ↓
recurse into children
        ↓
finer detail
```

and:

```text
small apparent solid angle
        ↓
node is visually small
        ↓
stop recursion
        ↓
use this node as the representation
```

This is the core of the multi-scale LOD behaviour. A node does not need to be close in absolute distance to require refinement; what matters is how large that region appears from the current viewpoint.

### 5.4 LOD thresholds

The current implementation uses three related solid-angle thresholds:

```text
solidAngleThreshold   = 2.4e-4
solidAngleFadeRange   = 1.2e-4
solidAnglePreloadRange= 6.0e-5
```

They serve different purposes:

- **LOD threshold:** controls when recursion into child nodes should continue.
- **Fade range:** provides a transition region for node visibility/weighting.
- **Preload range:** allows nodes to be loaded before they become fully required.

The exact values are project tuning parameters rather than fundamental constants.

### 5.5 Streaming

Once the LOD traversal has produced the desired node set, `GaiaMSLODLoader.cs` compares it with the nodes already resident in memory.

```text
Desired nodes
     │
     ├── already loaded → keep
     │
     ├── missing → start async load
     │
     └── no longer desired → unload
```

The particle files are loaded asynchronously so disk I/O does not block the main Unity thread.

The current loader also limits the number of simultaneous loads with:

```text
maxConcurrentLoads
```

A current working value is **16**. This limits large bursts of file reads and GPU-buffer uploads when many LOD nodes become necessary at once.

### 5.6 GPU upload

After a node is read and parsed, its stars are uploaded to a `GraphicsBuffer`.

The renderer therefore has a clean separation:

```text
Disk
 ↓
CPU node data
 ↓
GPU GraphicsBuffer
 ↓
Compute rasterizer
```

Only nodes selected by the LOD system need to reach the GPU.

### 5.7 Why this is similar to Gaia Sky

The important Gaia-Sky-like idea is that the full catalogue is organized into hierarchical spatial regions and the visible representation is chosen from that hierarchy according to apparent size.

The Unity implementation is independent, but the logical structure is similar:

```text
Huge catalogue
     ↓
Spatial hierarchy
     ↓
View-dependent node selection
     ↓
Coarse representation when small
     ↓
Fine representation when large
     ↓
Load only what is required
```

The current system therefore has the **core multi-scale / angular LOD behaviour** in place. Further work is mainly in tuning and adding stronger resource-management policies, rather than changing the basic LOD concept.

---

## 6. Runtime class responsibilities

### `GaiaIndexReader.cs`

Reads the original Gaia chunk index from:

```text
Data/Gaia/index.bin
```

It handles metadata describing the HEALPix/distance-shell catalogue layout.

It is part of the data organization pipeline, but the current MS LOD renderer does not use it as the active star-selection mechanism.

---

### `GaiaMSLODLoader.cs`

This is the **active runtime star loader and LOD controller**.

Main responsibilities:

- read the MS LOD metadata
- build the in-memory node hierarchy
- track the camera
- frustum-cull nodes
- compute node apparent solid angle
- decide LOD depth
- maintain desired node IDs
- asynchronously load particle files
- upload loaded stars into `GraphicsBuffer`s
- unload nodes that are no longer needed
- provide the currently loaded buffers to the renderer

It is the main class connecting the astronomical data representation to the rendering system.

---

### `GaiaStarBufferProvider.cs`

Defines the interface between the LOD/streaming system and the rendering system.

The renderer does not need to know whether stars came from HEALPix chunks, an octree, or another source. It simply asks the provider for the currently loaded GPU star buffers.

Conceptually:

```text
GaiaMSLODLoader
      │
      ▼
GaiaStarBufferProvider
      │
      ▼
GaiaStarRasterizer
```

This separation makes the rendering code independent of the specific streaming implementation.

---

### `GaiaStarRasterizer.cs`

Owns the GPU accumulation buffers and the configuration used by the compute rasterizer.

Its main responsibilities are:

- allocate/resize accumulation buffers
- gather the currently loaded star buffers
- provide camera/render parameters to the compute shader
- dispatch the rasterization kernels
- expose the accumulated buffers to the render feature

Important parameters include:

```text
Brightness multiplier
Storage scale       = 1 / 1024
Fixed-point scale   = 65536
Colour range        = -0.5 ... 3.5  (BP-RP)
```

The accumulation buffers store integer fixed-point quantities so multiple GPU threads can safely update the same pixel using atomic operations.

---

### `GaiaStarRenderFeature.cs`

This is the URP integration layer.

It creates and records the RenderGraph passes that connect:

```text
loaded star buffers
      ↓
compute rasterization
      ↓
HDR star composite
      ↓
glare pyramid
      ↓
final camera composite
```

The feature also gathers the loaded buffers from the `GaiaStarBufferProvider` and passes them through the rendering pipeline.

---

## 7. GPU star representation

The runtime star record is intentionally compact:

```text
struct StarGPU
{
    float3 position;
    float  magnitude;
    float  bpRp;
};
```

That is:

```text
3 × float position
1 × float magnitude
1 × float BP-RP colour index
--------------------------------
20 bytes per star
```

The GPU does not need the complete original Gaia record. It only receives the quantities required by the current visual renderer.

---

## 8. Compute rasterization

### `GaiaStarRasterizer.compute`

The compute shader is the heart of the star renderer.

The basic execution model is:

```text
one GPU thread
    ↓
one star
    ↓
project star to screen
    ↓
reject if outside the frustum
    ↓
find sub-pixel position
    ↓
calculate star contribution
    ↓
distribute to 2×2 neighbouring pixels
    ↓
atomic accumulation
```

### 8.1 2×2 sub-pixel distribution

Instead of forcing a star into exactly one pixel, its contribution is distributed across the nearest 2×2 pixel neighbourhood.

The weights depend on the fractional screen-space position of the star.

This gives smoother motion and reduces pixel-to-pixel popping as the camera moves.

### 8.2 Atomic fixed-point accumulation

Many stars can contribute to one pixel simultaneously.

Therefore the shader uses atomic integer accumulation rather than ordinary floating-point writes.

Conceptually:

```text
physical contribution
        ↓
× fixed-point scale
        ↓
uint atomic add
        ↓
accumulated integer
        ↓
decode during composite
```

The current fixed-point scale is:

```text
65536
```

---

## 9. Star compositing

### `GaiaStarComposite.shader`

This shader converts the accumulated integer buffers into an HDR star image.

The conceptual flow is:

```text
integer accumulation
        ↓
fixed-point decode
        ↓
HDR star luminance
        ↓
colour reconstruction from BP-RP
        ↓
StarHDR
```

The resulting `StarHDR` texture is kept separate from the camera image so the glare stage can operate on the high-dynamic-range star signal.

---

## 10. Glare pipeline

### `GaiaStarGlare.shader`

The glare system uses a multi-resolution pyramid.

Current levels are approximately:

```text
Glare0 = 1/2 resolution
Glare1 = 1/4 resolution
Glare2 = 1/8 resolution
Glare3 = 1/16 resolution
Glare4 = 1/32 resolution
```

The pipeline is:

```text
StarHDR
   ↓
Prefilter
   ↓
Glare0
   ↓
Downsample → Gaussian blur
   ↓
Glare1
   ↓
Downsample → Gaussian blur
   ↓
Glare2
   ↓
Downsample → Gaussian blur
   ↓
Glare3
   ↓
Downsample → Gaussian blur
   ↓
Glare4
```

### Pass layout

`GaiaStarGlare.shader` currently has four passes, and the C# RenderGraph code relies on these indices:

```text
Pass 0 = Prefilter
Pass 1 = Downsample
Pass 2 = HorizontalGaussian
Pass 3 = VerticalGaussian
```

Do not reorder these passes without updating the code that selects the shader pass indices.

---

## 11. Bright-star prefilter

The prefilter extracts the high-intensity part of the star image before the expensive blur operations.

Conceptually:

```text
brightness = max(R, G, B)
contribution = max(brightness - threshold, 0)
prefiltered = colour × contribution / brightness
```

This keeps glare concentrated on bright stars instead of blurring the entire catalogue equally.

---

## 12. Gaussian blur

Each glare level uses a separable blur:

```text
horizontal blur
      ↓
vertical blur
```

The implementation uses a compact multi-tap Gaussian approximation. Because the lower pyramid levels are already downsampled, the blur radius represents progressively larger angular structures at a low texture cost.

---

## 13. Perspective-correct glare

A simple screen-space blur assumes the screen is a flat Euclidean plane. That is not ideal for immersive rendering with a wide field of view.

The current implementation therefore conceptually performs:

```text
screen UV
   ↓
view-space ray
   ↓
offset the ray
   ↓
project the ray back to screen
   ↓
sample source texture
```

This makes the glare sampling dependent on the camera projection instead of using a fixed screen-space displacement everywhere.

The relevant helper functions in `GaiaStarGlare.shader` are:

```text
ViewRayFromUV()
ViewRayToUV()
SampleViewSpaceGaussian()
```

---

## 14. Final glare reconstruction

### `GaiaStarFinalComposite.shader`

The final shader combines:

```text
camera image
+ sharp StarHDR
+ glare pyramid
```

The current glare pyramid is weighted approximately as:

```text
Glare0 × 1
Glare1 × 1/2
Glare2 × 1/4
Glare3 × 1/8
Glare4 × 1/16
```

Conceptually:

```text
Final = Camera
      + SharpStars
      + GlareStrength ×
        (G0 + 0.5 G1 + 0.25 G2 + 0.125 G3 + 0.0625 G4)
```

The lower-resolution levels are reconstructed during the final composite so the result contains both a sharp star core and a broad surrounding halo.

---

## 15. RenderGraph resource flow

The important intermediate resources are:

```text
StarHDR
Glare0
Glare1
Glare2
Glare3
Glare4
```

These textures are produced and consumed by different RenderGraph passes.

For the global-texture handoff, the implementation uses the explicit RenderGraph mechanism:

```text
producer pass
    ↓
SetGlobalTextureAfterPass(...)
    ↓
consumer pass
    ↓
UseGlobalTexture(...)
```

This keeps the resource lifetime visible to RenderGraph instead of relying on unrestricted global command-buffer state.

---

## 16. XR / VR

The fullscreen shader path uses Unity's XR-aware texture macros such as:

```text
TEXTURE2D_X
SAMPLE_TEXTURE2D_X
```

and the render path supports mono or stereo accumulation buffers.

The current infrastructure is XR-aware, but **independent per-eye star projection remains an area for verification/improvement**. The current rasterization setup should therefore be treated as XR-compatible infrastructure rather than a finished independently projected stereo implementation until the left/right view-projection path is fully verified.

---

## 17. Performance design

The system is intended to scale with the visible/selected subset of Gaia rather than with the entire catalogue as live Unity objects.

### CPU

- Gaia is preprocessed offline.
- Runtime reads compact binary files rather than parsing large CSV files.
- LOD selection happens hierarchically instead of checking every star.
- Frustum culling rejects nodes outside the camera view.
- Background loading prevents file I/O from directly blocking the main thread.
- `maxConcurrentLoads` limits the number of simultaneous node loads.

### GPU

- one compute thread per star
- no GameObject per star
- no mesh renderer per star
- compact 20-byte star record
- atomic integer accumulation
- glare processed at progressively lower resolutions
- separable horizontal/vertical blur

### Memory

The intended memory model is:

```text
Full Gaia catalogue
        ↓
stored on disk as many LOD particle files
        ↓
only selected LOD nodes loaded into CPU memory
        ↓
only loaded nodes uploaded to GPU GraphicsBuffers
        ↓
screen-sized accumulation buffers
```

This avoids requiring the full Gaia catalogue to remain resident in VRAM.

---

## 18. Current status

At the current stage, the core rendering and LOD architecture is in place:

```text
[done]
Offline Gaia data preparation
        ↓
[done]
Hierarchical MS LOD data
        ↓
[done]
Frustum culling
        ↓
[done]
Solid-angle based LOD selection
        ↓
[done]
Asynchronous node streaming
        ↓
[done]
GPU GraphicsBuffer upload
        ↓
[done]
Compute star rasterization
        ↓
[done]
2×2 sub-pixel accumulation
        ↓
[done]
HDR star reconstruction
        ↓
[done]
5-level glare pipeline
        ↓
[done]
Final camera composite
```

The remaining work is primarily refinement rather than replacing the architecture. Important future areas include stereo verification, memory-budget policies, photometric calibration, and integration with dust/extinction.

---

## 19. Current limitations / future work

### Per-eye stereo projection

Use and verify independent left/right XR view-projection matrices for the compute rasterizer.

### Resident-star memory budget

A future streaming layer can impose a hard upper bound on the number of resident stars or GPU memory used by LOD nodes, then evict less useful nodes when the budget is exceeded.

### Better physical photometry

The current renderer is primarily a visual rendering system. Brightness multipliers, storage scale, and colour mapping are project parameters rather than a fully calibrated photometric model.

### Stellar colour model

The current GPU record uses BP-RP. Gaia astrophysical parameters can later be used for a more physically motivated colour/temperature mapping.

### Exposure and tone mapping

The renderer produces an HDR star signal. Final appearance can later be integrated more tightly with the project's global exposure and tone-mapping model.

### Dust / extinction integration

The rendering architecture leaves room for a later dust system that modifies stellar light through extinction and scattering before the final image is displayed.

---

## 20. Important implementation notes

### Keep glare shader pass indices stable

The C# code currently expects:

```text
0 Prefilter
1 Downsample
2 HorizontalGaussian
3 VerticalGaussian
```

Changing their order requires a corresponding C# change.

### Do not redeclare `_BlitTexture`

`Blit.hlsl` already provides `_BlitTexture` and its sampler infrastructure. Redeclaring it in individual glare passes causes shader redefinition errors.

### Use RenderGraph global-texture APIs correctly

For resources shared across RenderGraph passes, use the explicit global-texture mechanism:

```text
SetGlobalTextureAfterPass
UseGlobalTexture
```

rather than treating the pass command buffer as unrestricted global state.

### Keep C# / shader interfaces synchronized

The rendering C# side expects specific shader properties and buffers, including:

```text
_Stars
_StarCount
_StarLuminance
_StarColorLuminance
_Width
_Height
_EyeCount
_ViewProj0
_ViewProj1
_BrightnessMultiplier
_StorageScale
_FixedScale
_ColorMin
_ColorMax
```

Any change to these names or their layouts must be made on both sides.

---

## 21. What this implementation takes from the reference paper

The main ideas adopted from *Smaller than Pixels* are:

- render distant stars as sub-pixel point sources rather than expensive geometry
- process stars in a GPU software rasterizer
- process one star per GPU thread
- distribute a star's contribution over a 2×2 pixel neighbourhood using sub-pixel position
- preserve star brightness in HDR
- use a post-process glare representation rather than making every star's geometry large
- build glare at multiple resolutions
- reconstruct/combine the glare pyramid to produce a smooth extended glare
- account for perspective in immersive/wide-field glare rendering

The paper's implementation and this project are not identical.

Important project-specific differences include:

- Unity 6 + URP + RenderGraph instead of the original CosmoScout VR rendering framework
- Gaia DR3 local binary data instead of a simple bright-star subset
- HEALPix/distance-shell preprocessing followed by a runtime octree/MS LOD representation
- asynchronous local node streaming
- BP-RP colour information in the GPU star record
- fixed-point `uint` accumulation buffers
- a five-level RenderGraph glare pipeline
- Unity XR texture abstractions and VR integration

---

## 22. Reference implementation lineage

The ideas in this renderer are directly informed by the Eurographics 2025 paper listed below.

The paper states that the authors' implementation is part of **CosmoScout VR**, an open-source space-visualization project developed at the German Aerospace Center (DLR). CosmoScout VR also contains star-rendering functionality related to software-rasterized stars and perspective-aware glare.

This project does **not** claim to be CosmoScout VR or an official CosmoScout implementation.

---

## 23. References

### Primary rendering reference

Schneegans, S., Kreskowski, A., & Gerndt, A. (2025).
**Smaller than Pixels: Rendering Millions of Stars in Real-Time.**
Eurographics 2025 — Short Papers.

DOI:
https://doi.org/10.2312/egs.20251029

DLR publication page:
https://elib.dlr.de/215930/

PDF:
https://elib.dlr.de/215930/1/egs20251029.pdf

Eurographics Digital Library:
https://diglib.eg.org/items/fc13d662-0f6a-4a23-9ae2-d7f1ec1afc78

### Reference software

**CosmoScout VR** — open-source virtual-universe / scientific-visualization software associated with the paper.

Repository:
https://github.com/cosmoscout/cosmoscout-vr

The repository includes the `csp-stars` star-rendering plugin.

The paper authors' Gaia star data utility repository is also public:

https://github.com/cosmoscout/gaia-stars

### Gaia data

**Gaia Data Release 3 (DR3)**

Gaia ESA Archive:
https://gea.esac.esa.int/archive/

Gaia DR3 documentation:
https://gea.esac.esa.int/archive/documentation/GDR3/

Gaia DR3 overview:
https://www.cosmos.esa.int/web/gaia/data-release-3

### Gaia acknowledgement

The Gaia project requests the following acknowledgement when Gaia DR3 data are used:

> This work has made use of data from the European Space Agency (ESA) mission Gaia, processed by the Gaia Data Processing and Analysis Consortium (DPAC). Funding for the DPAC has been provided by national institutions, in particular the institutions participating in the Gaia Multilateral Agreement.

Official Gaia credit/citation instructions:
https://gea.esac.esa.int/archive/documentation/GDR3/Miscellaneous/sec_credit_and_citation_instructions/

The official Gaia guidance also asks users of Gaia DR3 data to cite the Gaia mission paper and the Gaia DR3 summary-of-contents paper.

### Unity / URP RenderGraph references

Unity documentation — blitting with RenderGraph in URP:
https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-blit.html

Unity documentation — global textures in URP RenderGraph:
https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-create-global-texture.html

Unity documentation — fullscreen blit / renderer features:
https://docs.unity3d.com/6000.0/Documentation/Manual/urp/renderer-features/how-to-fullscreen-blit.html

---

## 24. Credits and attribution

### Rendering method

This project uses ideas from:

**Simon Schneegans, Adrian Kreskowski, and Andreas Gerndt**

*Smaller than Pixels: Rendering Millions of Stars in Real-Time*,
Eurographics 2025.

Please retain the paper citation when the software-rasterization/glare approach is described in project documentation, presentations, reports, or publications.

### Gaia data

The stellar data originate from the **ESA Gaia mission**, processed by the **Gaia Data Processing and Analysis Consortium (DPAC)**.

Please retain the official Gaia acknowledgement and citation requirements when distributing or publishing results based on Gaia DR3 data.

### CosmoScout VR

The paper's reference implementation is part of **CosmoScout VR**. This project is an independent Unity implementation and should not be presented as an official CosmoScout VR component.

CosmoScout VR:
https://github.com/cosmoscout/cosmoscout-vr

### Unity

The renderer is built on Unity's Universal Render Pipeline (URP) and its RenderGraph system. Unity documentation is used as the API reference for the RenderGraph implementation.

---

## 25. Suggested citation for this project

A concise project description can use wording such as:

> The star renderer is an independent Unity/URP implementation inspired by Schneegans, Kreskowski, and Gerndt's Eurographics 2025 software-rasterization approach for sub-pixel star rendering and perspective-aware glare. Gaia DR3 is used as the stellar data source, with a hierarchical angular LOD and asynchronous local streaming system used to select and load the required star data at runtime.

---

## 26. Summary

The current system can be summarized as:

```text
Gaia DR3
  ↓
offline preprocessing
  ↓
HEALPix + distance-shell organization
  ↓
MS LOD octree / particle files
  ↓
frustum + solid-angle LOD selection
  ↓
asynchronous node streaming
  ↓
GPU GraphicsBuffers
  ↓
compute software rasterizer
  ↓
2×2 sub-pixel accumulation
  ↓
fixed-point HDR reconstruction
  ↓
StarHDR
  ↓
bright-star prefilter
  ↓
5-level glare pyramid
  ↓
perspective-aware Gaussian blur
  ↓
bicubic weighted reconstruction
  ↓
final camera composite
```

The result is a star-rendering system designed for a very large local Gaia dataset without creating a Unity object or mesh for every star. The LOD hierarchy determines which portions of that dataset are relevant to the current viewpoint, while the GPU rasterizer and HDR glare pipeline turn the selected stars into the final VR image.
