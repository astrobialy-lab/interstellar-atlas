# Gaia Star Rendering — Unity VR

This document describes the current star-rendering system used in the Unity VR space-simulation project.

The renderer is designed for a very large local Gaia DR3 catalogue and is built around the ideas in:

> **S. Schneegans, A. Kreskowski, and A. Gerndt, “Smaller than Pixels: Rendering Millions of Stars in Real-Time,” Eurographics 2025.**

The implementation here is an **independent Unity/URP implementation inspired by the paper**, not a copy of the CosmoScout VR implementation. The project uses the same central idea—treat distant stars as sub-pixel sources, rasterize them in a compute shader, then add a glare/post-processing stage—but adapts the data layout, streaming system, colour representation, and RenderGraph integration to this project.

---

## 1. Overview

The rendering pipeline is:

```text
Gaia DR3 data
    │
    ▼
Offline preprocessing
    │
    ├── HEALPix spatial partitioning (level 6)
    ├── Distance-shell partitioning
    └── Binary chunk files
    │
    ▼
GaiaIndexReader
    │
    ▼
GaiaStarLoader
    │
    │  Streams only nearby/relevant chunks
    │
    ▼
Loaded GraphicsBuffers
    │
    ▼
GaiaStarRasterizer.compute
    │
    │  one GPU thread per star
    │  project star to screen
    │  distribute sub-pixel contribution over 2×2 pixels
    │  atomic fixed-point accumulation
    │
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
    ├── Prefilter bright stars
    ├── Downsample
    ├── Horizontal Gaussian
    └── Vertical Gaussian
    │
    ▼
5-level glare pyramid
    │
    ▼
GaiaStarFinalComposite.shader
    │
    ├── original camera image
    ├── sharp stars
    └── weighted bicubic glare pyramid
    │
    ▼
Final camera image
```

The important design choice is that stars are **not rendered as conventional 3D meshes or billboard geometry**. Most astronomical stars are far smaller than a display pixel at normal viewing distances, so the renderer treats them as point-like sources and preserves their luminance by distributing their contribution across nearby pixels.

---

## 2. Why this approach

A conventional point primitive has two problems for this use case:

1. A star can move between discrete pixel locations as the camera moves, producing visible flicker/jitter.
2. A one-pixel representation does not naturally preserve the apparent luminance behaviour required for very wide fields of view.

The reference paper addresses this by using a software rasterizer. Each star is processed independently, projected into screen space, and its contribution is distributed over a 2×2 pixel neighbourhood according to the star's sub-pixel position.

This project follows that general strategy, with the accumulation implemented using integer buffers and a fixed-point scale so that multiple stars can safely contribute to the same pixel using atomic operations.

---

## 3. Project file structure

The Unity-side rendering code is organized as follows:

```text
Assets/
├── Scripts/
│   └── Gaia/
│       ├── GaiaIndexReader.cs
│       ├── GaiaStarLoader.cs
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
    └── chunks/
        ├── healpix_00000/
        │   ├── shell_00.bin
        │   ├── shell_01.bin
        │   └── ...
        ├── healpix_00001/
        │   └── ...
        └── ...
```

The exact number of chunk directories/files depends on the preprocessing run. The current processed catalogue uses HEALPix level 6 and 10 distance shells, with a generated index describing the available chunk files.

---

## 4. Data organization

### 4.1 Gaia source data

The renderer uses Gaia DR3 as the underlying stellar catalogue.

The raw Gaia download is kept outside the runtime rendering path. Unity does **not** query Gaia over the network while rendering.

Instead, Gaia data is converted offline into compact binary chunks suitable for streaming at runtime.

### 4.2 Spatial partitioning

The current partitioning scheme is:

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

This gives two useful properties:

- angular/spatial locality from HEALPix
- radial locality from the distance shells

The renderer therefore does not need one enormous GPU buffer containing the whole Gaia catalogue.

### 4.3 Index

`Data/Gaia/index.bin` contains the metadata needed to locate chunk files.

The current processed index contains approximately:

```text
Magic:      0x47414941
Version:    2
HEALPix:    level 6
Shells:     10
Chunks:     219,646
```

The index also contains the chunk path string table and the spatial metadata used when deciding which chunks need to be loaded.

---

## 5. Runtime classes

### `GaiaIndexReader.cs`

Responsible for reading `index.bin` and exposing chunk metadata to the runtime.

Main responsibilities:

- load the Gaia binary index
- parse HEALPix/shell chunk metadata
- resolve chunk paths
- expose chunk spatial bounds
- allow the loader to determine which chunks intersect the active viewing region

The index is metadata only. The actual stars remain in the individual chunk files.

---

### `GaiaStarLoader.cs`

Responsible for runtime chunk streaming.

Main responsibilities:

- determine which chunks should be resident around the camera
- asynchronously read binary chunk files
- convert loaded star records into GPU-friendly `GraphicsBuffer` objects
- unload chunks that are sufficiently far away
- maintain the current list of loaded star buffers for the renderer

Current streaming parameters include a load radius and an unload radius so that chunks are not repeatedly loaded/unloaded when the camera moves slightly.

This separation is important: the renderer only sees the currently resident GPU buffers and does not care how the files were loaded.

---

### `GaiaStarRasterizer.cs`

Owns the GPU accumulation buffers and the configuration used by the compute rasterizer.

Current important settings include:

```text
Brightness multiplier = 1000
Storage scale         = 1 / 1024
Fixed-point scale     = 65536
Colour range          = -0.5 ... 3.5  (BP-RP)
```

The renderer stores accumulated data in two GPU buffers:

```text
starLuminance
starColorLuminance
```

Each buffer contains one `uint` per render pixel (per eye when stereo rendering is used).

The fixed-point representation is conceptually:

```text
storedValue = physicalValue × 65536
```

This permits atomic integer accumulation while retaining fractional precision.

---

### `GaiaStarRenderFeature.cs`

This is the URP integration layer.

It creates and records the RenderGraph passes that connect the compute rasterizer, the star-composite shader, the glare pyramid, and the final camera composite.

At a high level its work is:

```text
1. Get active camera target
2. Determine render resolution and eye count
3. Gather currently loaded star GraphicsBuffers
4. Run Gaia star compute rasterization
5. Composite accumulated buffers into an HDR star texture
6. Build the glare pyramid
7. Export intermediate textures through RenderGraph globals
8. Run the final composite into the camera target
```

The implementation uses Unity's modern RenderGraph resource declarations rather than treating the command buffer as an unrestricted global-state API.

In particular, intermediate textures are exported with `SetGlobalTextureAfterPass` and consumed with `UseGlobalTexture`, which is the URP RenderGraph pattern documented by Unity.

---

## 6. GPU star representation

The runtime star record is intentionally compact.

Conceptually it is:

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

The renderer does not need to carry the original Gaia record into the GPU.

The runtime renderer uses the preprocessed position, apparent-brightness information, and BP-RP colour information needed for the visual representation.

---

## 7. Compute rasterization

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

### 7.1 2×2 sub-pixel distribution

Instead of forcing the star into exactly one pixel, its contribution is distributed across the nearest 2×2 pixel neighbourhood.

The weights depend on the fractional screen-space position of the star.

This has two important effects:

- smoother motion when the camera moves
- reduced pixel-to-pixel popping compared with single-pixel rendering

It is also the key idea from the reference paper's software rasterizer.

### 7.2 Atomic accumulation

Many stars can contribute to the same pixel simultaneously.

Therefore the compute shader cannot simply write:

```text
buffer[pixel] = value
```

Instead it uses atomic integer accumulation.

The project stores the contribution using a fixed-point representation, so the atomic operation can operate on `uint` values while the final composite converts the value back into floating-point HDR values.

---

## 8. Star compositing

### `GaiaStarComposite.shader`

This shader converts the accumulated integer buffers into an HDR star image.

Inputs:

```text
starLuminance
starColorLuminance
```

The composite reconstructs the per-pixel star luminance and colour and writes it to an HDR render texture.

Conceptually:

```text
uint accumulation
        ↓
fixed-point decode
        ↓
HDR star luminance
        ↓
colour reconstruction from BP-RP
        ↓
StarHDR
```

The resulting `StarHDR` texture is intentionally separate from the camera image. This is important because the glare filter must operate on the high-dynamic-range star signal rather than on the already-tonemapped screen image.

---

## 9. Glare pipeline

### `GaiaStarGlare.shader`

The glare system is a multi-resolution pyramid.

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

`GaiaStarGlare.shader` currently has four passes, and the C# RenderGraph code relies on these exact indices:

```text
Pass 0 = Prefilter
Pass 1 = Downsample
Pass 2 = HorizontalGaussian
Pass 3 = VerticalGaussian
```

Do not reorder these passes without updating the RenderGraph code that selects the shader pass index.

---

## 10. Bright-star prefilter

The prefilter extracts the high-intensity part of the star image.

Conceptually:

```text
brightness = max(R, G, B)

contribution = max(brightness - threshold, 0)

prefiltered = color × contribution / brightness
```

The purpose is to keep the glare contribution concentrated on sufficiently bright stars instead of blurring every star in the catalogue.

---

## 11. Gaussian blur

Each pyramid level is blurred with a separable Gaussian approximation:

```text
horizontal blur
      ↓
vertical blur
```

The current implementation uses a 5-tap optimized Gaussian approximation with weights based on the common pair of symmetric taps around the center.

The blur is performed independently for the horizontal and vertical directions.

The current view-space version additionally maps the sampling displacement through the camera projection so that the glare is not purely screen-space.

---

## 12. Perspective-correct glare

A normal screen-space blur assumes that a fixed number of screen pixels corresponds to a fixed angular distance everywhere on the screen.

That is not correct for immersive/wide-field rendering.

The current glare implementation therefore does the conceptual operation:

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

This makes the blur dependent on the camera projection instead of treating the screen as a flat Euclidean image.

That is the reason for the view-space helper functions in `GaiaStarGlare.shader`:

```text
ViewRayFromUV()
ViewRayToUV()
SampleViewSpaceGaussian()
```

The reference paper specifically discusses perspective-correct glare as an important consideration for immersive scenarios with large fields of view.

---

## 13. Final glare reconstruction

### `GaiaStarFinalComposite.shader`

The final shader combines three components:

```text
camera image
+ sharp StarHDR
+ glare pyramid
```

The five glare pyramid levels are weighted by decreasing powers of two:

```text
Glare0 × 1
Glare1 × 1/2
Glare2 × 1/4
Glare3 × 1/8
Glare4 × 1/16
```

The current final shader reconstructs the lower-resolution levels using bicubic sampling before adding them to the sharp stars.

Conceptually:

```text
Final = Camera
      + SharpStars
      + GlareStrength ×
        (G0 + 0.5 G1 + 0.25 G2 + 0.125 G3 + 0.0625 G4)
```

This keeps the sharp star core while building a broad HDR halo around bright sources.

---

## 14. RenderGraph resource flow

The renderer passes several intermediate textures between RenderGraph passes.

The important resources are conceptually:

```text
StarHDR
Glare0
Glare1
Glare2
Glare3
Glare4
```

Because the final composite shader consumes them as global textures, the RenderGraph code uses the explicit global-texture transfer mechanism:

```text
producer pass
    ↓
SetGlobalTextureAfterPass(...)
    ↓
consumer pass
    ↓
UseGlobalTexture(...)
```

This is preferable to calling `CommandBuffer.SetGlobalTexture` from inside an ordinary RenderGraph render function, which is not allowed for the global-state model used by the current URP RenderGraph implementation.

---

## 15. Why the star image and glare are separate

The sharp stars and the glare should remain separate buffers.

If the stars are blurred before their sharp contribution is preserved, the renderer loses the compact point-source core.

Keeping them separate gives:

```text
StarHDR  → sharp point/source
Glare    → soft extended response
```

and the final shader combines them only at the end.

This also makes it possible to tune `GlareStrength` independently of the underlying star brightness.

---

## 16. XR / VR status

The shader code uses Unity's XR-aware texture macros such as:

```text
TEXTURE2D_X
SAMPLE_TEXTURE2D_X
```

and the fullscreen shaders call the stereo setup macro where needed.

The render path also allocates accumulation buffers for an eye count of 1 or 2.

However, **full per-eye star projection is still an area for further verification/improvement**. The current C# rasterization setup passes the same `viewProj` matrix into both `_ViewProj0` and `_ViewProj1` rather than supplying independent left/right eye matrices.

Therefore the system should be considered:

```text
XR-compatible infrastructure: yes
Full independently projected stereo star rasterization: not finished yet
```

This is an important future step before treating the renderer as fully stereo-correct.

---

## 17. What this implementation takes from the paper

The main ideas adopted from *Smaller than Pixels* are:

- render distant stars as sub-pixel point sources rather than expensive geometry
- process stars in a GPU software rasterizer
- process one star per GPU thread
- distribute a star's contribution over a 2×2 pixel neighbourhood using sub-pixel position
- keep stellar brightness in HDR
- use a post-process glare representation rather than making every star's geometry large
- build glare at multiple resolutions
- reconstruct/combine the pyramid to produce a smooth extended glare
- account for perspective in immersive/wide-field glare rendering

The paper's implementation and this project are not identical.

Important project-specific differences include:

- Unity 6 + URP + RenderGraph instead of the original CosmoScout VR rendering framework
- Gaia DR3 local binary chunk streaming instead of a simple bright-star CSV subset
- HEALPix + distance shells for out-of-core streaming
- BP-RP colour information in the GPU star record
- fixed-point `uint` accumulation buffers
- a five-level RenderGraph glare pipeline
- Unity XR texture abstractions and VR integration

---

## 18. Performance design goals

The renderer is structured so that the expensive work scales primarily with the stars that are actually resident/processed, rather than requiring every Gaia source to exist as a full Unity GameObject or mesh.

Important performance decisions:

### CPU

- Gaia is preprocessed offline.
- Runtime loads binary chunks instead of parsing large CSV files every frame.
- Only a neighbourhood around the camera is kept resident.
- Star chunks are stored in GPU-friendly buffers.

### GPU

- one compute thread per star
- no GameObject per star
- no mesh renderer per star
- compact 20-byte star record
- atomic integer accumulation
- separable Gaussian blur
- glare processed at progressively lower resolutions

### Memory

The system deliberately keeps:

```text
CPU disk data → many chunks
GPU resident data → only currently loaded chunks
screen accumulation → 2 × uint buffers per pixel
```

rather than trying to keep the entire Gaia catalogue in VRAM.

---

## 19. Current limitations / future work

### Per-eye stereo projection

Use independent left/right XR view-projection matrices in the compute rasterizer.

### Better physical photometry

The current renderer is primarily a visual rendering system. The brightness multiplier, storage scale, and colour mapping are project parameters and are not yet a complete calibrated photometric model.

A later physically calibrated stage can convert Gaia quantities into display luminance more rigorously.

### Stellar colour model

The current GPU record uses BP-RP. A future version can incorporate additional Gaia astrophysical parameters and/or an explicit stellar-temperature colour model where appropriate.

### LOD

The long-term plan is to make star representation and processing depend on projected/angular size rather than simply cutting stars by catalogue brightness.

### Exposure and tone mapping

The current star renderer produces an HDR signal. The final display appearance should eventually be integrated with the project's global exposure and tone-mapping model rather than relying on arbitrary visual multipliers.

### Dust/extinction integration

The renderer is intentionally separated so that later dust rendering can modify the light reaching the camera. The planned dust system can eventually apply extinction/scattering to the stellar image before or during final composition.

---

## 20. Important implementation notes

### Keep shader pass indices stable

The glare shader's current pass indices are part of the C# interface:

```text
0 Prefilter
1 Downsample
2 HorizontalGaussian
3 VerticalGaussian
```

Changing these orderings requires changing the RenderGraph code.

### Do not redeclare `_BlitTexture`

`Blit.hlsl` already supplies `_BlitTexture` and its sampler infrastructure. Redeclaring it in the individual glare passes causes shader redefinition errors.

### Do not set RenderGraph globals from the pass command buffer

Use:

```text
SetGlobalTextureAfterPass
UseGlobalTexture
```

rather than `CommandBuffer.SetGlobalTexture` inside a RenderGraph pass.

### Keep `GaiaStarRenderFeature.cs` and the shader interfaces synchronized

The C# side expects specific property names and shader pass indices, including:

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

---

## 21. Reference implementation lineage

The ideas in this renderer are directly informed by the Eurographics 2025 paper listed below.

The paper states that the authors' implementation is part of **CosmoScout VR**, an open-source space-visualization project developed at the German Aerospace Center (DLR). CosmoScout VR's current public repository also contains star rendering functionality, including software-rasterized points and perspective-correct HDR glaring.

This project does **not** claim to be CosmoScout VR or an official CosmoScout implementation.

---

## 22. References

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

## 23. Credits and attribution

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

## 24. Suggested citation for this project

A concise project description can use wording such as:

> The star renderer is an independent Unity/URP implementation inspired by Schneegans, Kreskowski, and Gerndt's Eurographics 2025 software-rasterization approach for sub-pixel star rendering and perspective-aware glare. Gaia DR3 is used as the stellar data source.

---

## 25. Summary

The current system can be summarized as:

```text
Gaia DR3
  ↓
offline binary preprocessing
  ↓
HEALPix level 6 + distance shells
  ↓
indexed chunk files
  ↓
runtime chunk streaming
  ↓
GPU star buffers
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

The result is a star-rendering path designed to scale to a very large Gaia dataset without creating a Unity object or mesh for every star, while preserving sub-pixel motion behaviour and providing an HDR glare effect suitable for a wide-field VR space visualization.
