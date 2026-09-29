using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;


public class GaiaStarRenderFeature :
    ScriptableRendererFeature
{
    [SerializeField]
    private Shader compositeShader;

    [SerializeField]
    private Shader glareShader;

    [SerializeField]
    private Shader finalCompositeShader;

    [SerializeField]
    private GaiaStarRasterizer rasterizer;

    [SerializeField]
    private GaiaFarStarCache farStarCache;

    [SerializeField]
    private bool farCacheOnly;

    private GaiaStarRenderPass renderPass;


    public override void Create()
    {
        if (compositeShader == null)
        {
            compositeShader =
                Shader.Find(
                    "Hidden/GaiaStarComposite"
                );
        }

        if (glareShader == null)
        {
            glareShader =
                Shader.Find(
                    "Custom/GaiaStarGlare"
                );
        }

        if (finalCompositeShader == null)
        {
            finalCompositeShader =
                Shader.Find(
                    "Hidden/GaiaStarFinalComposite"
                );
        }


        renderPass =
            new GaiaStarRenderPass(
                compositeShader,
                glareShader,
                finalCompositeShader,
                rasterizer,
                farStarCache
            );

        renderPass.renderPassEvent =
            RenderPassEvent.AfterRenderingTransparents;
    }


    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData
    )
    {
        CameraData cameraData =
            renderingData.cameraData;


        if (cameraData.cameraType !=
            CameraType.Game)
        {
            return;
        }


        if (cameraData.renderType ==
            CameraRenderType.Overlay)
        {
            return;
        }


        if (rasterizer == null)
        {
            rasterizer =
                Object.FindAnyObjectByType<
                    GaiaStarRasterizer
                >();

            renderPass.SetRasterizer(
                rasterizer
            );
        }


        if (farStarCache == null)
        {
            farStarCache =
                Object.FindAnyObjectByType<
                    GaiaFarStarCache
                >();

            renderPass.SetFarStarCache(
                farStarCache
            );
        }


        renderPass.SetFarCacheOnly(
            farCacheOnly
        );


        if (rasterizer == null)
            return;


        if (!rasterizer.enabled)
            return;


        renderer.EnqueuePass(
            renderPass
        );
    }


    protected override void Dispose(
        bool disposing
    )
    {
        if (renderPass != null)
        {
            renderPass.Dispose();
            renderPass = null;
        }
    }


    private class GaiaStarRenderPass :
        ScriptableRenderPass
    {
        private const int PYRAMID_LEVELS = 5;


        private Material starCompositeMaterial;
        private Material glareMaterial;
        private Material finalCompositeMaterial;


        private GaiaStarRasterizer rasterizer;
        private GaiaFarStarCache farStarCache;

        private bool farCacheOnly;


        private GraphicsBuffer starLuminance;
        private GraphicsBuffer starColorLuminance;


        private RTHandle farCacheRTHandle;
        private RenderTexture farCacheTextureReference;


        private int bufferWidth;
        private int bufferHeight;
        private int bufferEyeCount;


        private readonly List<GraphicsBuffer> starBuffers =
            new List<GraphicsBuffer>();

        private readonly List<int> starCounts =
            new List<int>();


        private readonly List<BufferHandle> starBufferHandles =
            new List<BufferHandle>();

        private readonly List<int> starCountArray =
            new List<int>();

        private readonly List<float> starFades =
            new List<float>();


        private static readonly int WidthID =
            Shader.PropertyToID("_Width");

        private static readonly int HeightID =
            Shader.PropertyToID("_Height");

        private static readonly int EyeCountID =
            Shader.PropertyToID("_EyeCount");

        private static readonly int StarCountID =
            Shader.PropertyToID("_StarCount");

        private static readonly int StarLuminanceID =
            Shader.PropertyToID("_StarLuminance");

        private static readonly int StarColorLuminanceID =
            Shader.PropertyToID("_StarColorLuminance");

        private static readonly int StarsID =
            Shader.PropertyToID("_Stars");

        private static readonly int ViewProj0ID =
            Shader.PropertyToID("_ViewProj0");

        private static readonly int ViewProj1ID =
            Shader.PropertyToID("_ViewProj1");

        private static readonly int BrightnessID =
            Shader.PropertyToID("_BrightnessMultiplier");

        private static readonly int StorageScaleID =
            Shader.PropertyToID("_StorageScale");

        private static readonly int FixedScaleID =
            Shader.PropertyToID("_FixedScale");

        private static readonly int ColorMinComputeID =
            Shader.PropertyToID("_ColorMin");

        private static readonly int ColorMaxComputeID =
            Shader.PropertyToID("_ColorMax");

        private static readonly int StorageScaleInvID =
            Shader.PropertyToID("_StorageScaleInv");

        private static readonly int ColorMinCompositeID =
            Shader.PropertyToID("_ColorMin");

        private static readonly int ColorMaxCompositeID =
            Shader.PropertyToID("_ColorMax");


        private static readonly int GlareThresholdID =
            Shader.PropertyToID("_GlareThreshold");

        private static readonly int GlareSoftnessID =
            Shader.PropertyToID("_GlareSoftness");

        private static readonly int GlareStrengthID =
            Shader.PropertyToID("_GlareStrength");


        private static readonly int StarHDRID =
            Shader.PropertyToID("_StarHDR");

        private static readonly int Glare0ID =
            Shader.PropertyToID("_Glare0");

        private static readonly int Glare1ID =
            Shader.PropertyToID("_Glare1");

        private static readonly int Glare2ID =
            Shader.PropertyToID("_Glare2");

        private static readonly int Glare3ID =
            Shader.PropertyToID("_Glare3");

        private static readonly int Glare4ID =
            Shader.PropertyToID("_Glare4");

        private static readonly int LODFadeID =
            Shader.PropertyToID("_LOD_Fade");


        // ============================================================
        // Far-cache shader properties
        // ============================================================

        private static readonly int FarStarCacheID =
            Shader.PropertyToID("_FarStarCache");

        private static readonly int FarStarCacheValidID =
            Shader.PropertyToID("_FarStarCacheValid");

        private static readonly int FarStarCacheOnlyID =
            Shader.PropertyToID("_FarStarCacheOnly");

        private static readonly int InvViewProj0ID =
            Shader.PropertyToID("_InvViewProj0");

        private static readonly int InvViewProj1ID =
            Shader.PropertyToID("_InvViewProj1");

        private static readonly int EyePosition0ID =
            Shader.PropertyToID("_EyePosition0");

        private static readonly int EyePosition1ID =
            Shader.PropertyToID("_EyePosition1");


        // ============================================================
        // Live rasterizer cutoff properties
        // ============================================================

        private static readonly int UseFarCacheCutoffID =
            Shader.PropertyToID("_UseFarCacheCutoff");

        private static readonly int FarCachePositionID =
            Shader.PropertyToID("_FarCachePosition");

        private static readonly int FarCacheDistanceID =
            Shader.PropertyToID("_FarCacheDistance");


        public GaiaStarRenderPass(
            Shader compositeShader,
            Shader glareShader,
            Shader finalCompositeShader,
            GaiaStarRasterizer rasterizer,
            GaiaFarStarCache farStarCache
        )
        {
            this.rasterizer =
                rasterizer;

            this.farStarCache =
                farStarCache;


            if (compositeShader != null)
            {
                starCompositeMaterial =
                    CoreUtils.CreateEngineMaterial(
                        compositeShader
                    );
            }


            if (glareShader != null)
            {
                glareMaterial =
                    CoreUtils.CreateEngineMaterial(
                        glareShader
                    );

                glareMaterial.SetFloat(
                    GlareThresholdID,
                    1.0f
                );

                glareMaterial.SetFloat(
                    GlareSoftnessID,
                    2.0f
                );
            }


            if (finalCompositeShader != null)
            {
                finalCompositeMaterial =
                    CoreUtils.CreateEngineMaterial(
                        finalCompositeShader
                    );

                finalCompositeMaterial.SetFloat(
                    GlareStrengthID,
                    1.0f
                );
            }
        }


        public void SetRasterizer(
            GaiaStarRasterizer value
        )
        {
            rasterizer =
                value;
        }


        public void SetFarStarCache(
            GaiaFarStarCache value
        )
        {
            farStarCache =
                value;
        }


        public void SetFarCacheOnly(
            bool value
        )
        {
            farCacheOnly =
                value;
        }


        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData
        )
        {
            if (rasterizer == null ||
                !rasterizer.enabled ||
                rasterizer.RasterizerCompute == null ||
                rasterizer.StarLoader == null ||
                starCompositeMaterial == null ||
                glareMaterial == null ||
                finalCompositeMaterial == null)
            {
                return;
            }


            UniversalResourceData resourceData =
                frameData.Get<
                    UniversalResourceData
                >();


            UniversalCameraData cameraData =
                frameData.Get<
                    UniversalCameraData
                >();


            TextureHandle colorTarget =
                resourceData.activeColorTexture;


            if (!colorTarget.IsValid())
                return;


            Camera camera =
                cameraData.camera;


            if (camera == null)
                return;


            int width =
                cameraData
                    .cameraTargetDescriptor
                    .width;

            int height =
                cameraData
                    .cameraTargetDescriptor
                    .height;


            int eyeCount =
                camera.stereoEnabled
                ? 2
                : 1;


            if (!EnsureBuffers(
                    width,
                    height,
                    eyeCount
                ))
            {
                return;
            }


            // ============================================================
            // Get currently loaded Gaia star buffers
            // ============================================================

            starBuffers.Clear();
            starCounts.Clear();
            starFades.Clear();

            rasterizer.GetLoadedStarBuffers(
                starBuffers,
                starCounts,
                starFades
            );


            starBufferHandles.Clear();
            starCountArray.Clear();


            for (int i = 0;
                 i < starBuffers.Count;
                 i++)
            {
                GraphicsBuffer buffer =
                    starBuffers[i];

                int count =
                    starCounts[i];


                if (buffer == null ||
                    count <= 0)
                {
                    continue;
                }


                BufferHandle handle =
                    renderGraph.ImportBuffer(
                        buffer
                    );


                starBufferHandles.Add(
                    handle
                );

                starCountArray.Add(
                    count
                );
            }


            BufferHandle luminanceHandle =
                renderGraph.ImportBuffer(
                    starLuminance
                );


            BufferHandle colorLuminanceHandle =
                renderGraph.ImportBuffer(
                    starColorLuminance
                );


            // ============================================================
            // Camera matrices
            // ============================================================

            Matrix4x4 viewProjLeft;
            Matrix4x4 viewProjRight;

            Matrix4x4 invViewProjLeft;
            Matrix4x4 invViewProjRight;

            Vector4 eyePositionLeft;
            Vector4 eyePositionRight;


            if (cameraData.xr.enabled &&
                cameraData.xr.viewCount >= 2)
            {
                Matrix4x4 viewLeft =
                    cameraData.xr.GetViewMatrix(0);

                Matrix4x4 projLeft =
                    GL.GetGPUProjectionMatrix(
                        cameraData.xr.GetProjMatrix(0),
                        true
                    );

                Matrix4x4 viewRight =
                    cameraData.xr.GetViewMatrix(1);

                Matrix4x4 projRight =
                    GL.GetGPUProjectionMatrix(
                        cameraData.xr.GetProjMatrix(1),
                        true
                    );

                viewProjLeft =
                    projLeft * viewLeft;

                viewProjRight =
                    projRight * viewRight;

                invViewProjLeft =
                    viewProjLeft.inverse;

                invViewProjRight =
                    viewProjRight.inverse;


                Matrix4x4 eyeWorldLeft =
                    viewLeft.inverse;

                Matrix4x4 eyeWorldRight =
                    viewRight.inverse;


                Vector3 eyeLeft =
                    eyeWorldLeft.GetColumn(3);

                Vector3 eyeRight =
                    eyeWorldRight.GetColumn(3);


                eyePositionLeft =
                    new Vector4(
                        eyeLeft.x,
                        eyeLeft.y,
                        eyeLeft.z,
                        1.0f
                    );

                eyePositionRight =
                    new Vector4(
                        eyeRight.x,
                        eyeRight.y,
                        eyeRight.z,
                        1.0f
                    );
            }
            else
            {
                Matrix4x4 view =
                    camera.worldToCameraMatrix;

                Matrix4x4 proj =
                    GL.GetGPUProjectionMatrix(
                        camera.projectionMatrix,
                        true
                    );

                Matrix4x4 viewProj =
                    proj * view;

                viewProjLeft =
                    viewProj;

                viewProjRight =
                    viewProj;

                invViewProjLeft =
                    viewProj.inverse;

                invViewProjRight =
                    invViewProjLeft;


                Vector3 eye =
                    camera.transform.position;


                eyePositionLeft =
                    new Vector4(
                        eye.x,
                        eye.y,
                        eye.z,
                        1.0f
                    );

                eyePositionRight =
                    eyePositionLeft;
            }


            // ============================================================
            // Far-cache state
            // ============================================================

            bool farCacheValid =
                false;

            RenderTexture farCacheTexture =
                null;

            TextureHandle farCacheHandle =
                TextureHandle.nullHandle;


            Vector3 farCachePosition =
                camera.transform.position;

            float farCacheDistance =
                0.0f;


            if (farStarCache != null)
            {
                farCacheDistance =
                    farStarCache.FarCacheDistance;


                if (farStarCache.CacheValid &&
                    farStarCache.CacheTexture != null &&
                    farStarCache.CacheTexture.IsCreated())
                {
                    farCacheTexture =
                        farStarCache.CacheTexture;

                    UpdateFarCacheRTHandle(
                        farCacheTexture
                    );


                    if (farCacheRTHandle != null)
                    {
                        farCacheHandle =
                            renderGraph.ImportTexture(
                                farCacheRTHandle
                            );

                        farCacheValid =
                            true;

                        farCachePosition =
                            farStarCache.CachePosition;
                    }
                }
            }


            // ============================================================
            // Gaia star rasterization
            // ============================================================

            using (
                var builder =
                    renderGraph.AddComputePass<
                        ComputePassData
                    >(
                        "Gaia Star Rasterization",
                        out ComputePassData computeData
                    )
            )
            {
                computeData.computeShader =
                    rasterizer.RasterizerCompute;

                computeData.clearKernel =
                    rasterizer.ClearKernel;

                computeData.rasterizeKernel =
                    rasterizer.RasterizeKernel;

                computeData.luminance =
                    luminanceHandle;

                computeData.colorLuminance =
                    colorLuminanceHandle;

                computeData.starBuffers =
                    starBufferHandles.ToArray();

                computeData.starCounts =
                    starCountArray.ToArray();

                computeData.starFades =
                    starFades.ToArray();

                computeData.width =
                    width;

                computeData.height =
                    height;

                computeData.eyeCount =
                    eyeCount;

                computeData.brightnessMultiplier =
                    rasterizer.BrightnessMultiplier;

                computeData.storageScale =
                    rasterizer.StorageScale;

                computeData.fixedScale =
                    rasterizer.FixedScale;

                computeData.colorMin =
                    rasterizer.ColorMin;

                computeData.colorMax =
                    rasterizer.ColorMax;

                computeData.viewProj0 =
                    viewProjLeft;

                computeData.viewProj1 =
                    viewProjRight;

                computeData.useFarCacheCutoff =
                    farCacheValid;

                computeData.farCachePosition =
                    farCachePosition;

                computeData.farCacheDistance =
                    farCacheDistance;


                builder.UseBuffer(
                    computeData.luminance,
                    AccessFlags.Write
                );

                builder.UseBuffer(
                    computeData.colorLuminance,
                    AccessFlags.Write
                );


                for (
                    int i = 0;
                    i < computeData.starBuffers.Length;
                    i++
                )
                {
                    builder.UseBuffer(
                        computeData.starBuffers[i],
                        AccessFlags.Read
                    );
                }


                builder.AllowPassCulling(false);


                builder.SetRenderFunc(
                    (
                        ComputePassData data,
                        ComputeGraphContext context
                    ) =>
                        ExecuteComputePass(
                            data,
                            context
                        )
                );
            }


            // ============================================================
            // Full-resolution HDR star texture
            // ============================================================

            TextureHandle starHDR =
                CreateHDRTexture(
                    renderGraph,
                    cameraData.cameraTargetDescriptor,
                    width,
                    height,
                    "Gaia Star HDR"
                );


            using (
                var builder =
                    renderGraph.AddRasterRenderPass<
                        StarBufferPassData
                    >(
                        "Gaia Star Buffer To HDR",
                        out StarBufferPassData data
                    )
            )
            {
                data.material =
                    starCompositeMaterial;

                data.luminanceBuffer =
                    starLuminance;

                data.colorLuminanceBuffer =
                    starColorLuminance;

                data.output =
                    starHDR;

                data.width =
                    width;

                data.height =
                    height;

                data.eyeCount =
                    eyeCount;

                data.storageScaleInverse =
                    rasterizer.StorageScaleInverse;

                data.fixedScale =
                    rasterizer.FixedScale;

                data.colorMin =
                    rasterizer.ColorMin;

                data.colorMax =
                    rasterizer.ColorMax;


                // Far cache
                data.farCacheTexture =
                    farCacheTexture;

                data.farCacheHandle =
                    farCacheHandle;

                data.farCacheValid =
                    farCacheValid;

                data.farCacheOnly =
                    farCacheOnly;


                // Per-eye inverse camera transforms
                data.invViewProj0 =
                    invViewProjLeft;

                data.invViewProj1 =
                    invViewProjRight;

                data.eyePosition0 =
                    eyePositionLeft;

                data.eyePosition1 =
                    eyePositionRight;


                builder.UseBuffer(
                    luminanceHandle,
                    AccessFlags.Read
                );

                builder.UseBuffer(
                    colorLuminanceHandle,
                    AccessFlags.Read
                );


                for (int i = 0;
                     i < starBufferHandles.Count;
                     i++)
                {
                    builder.UseBuffer(
                        starBufferHandles[i],
                        AccessFlags.Read
                    );
                }


                if (farCacheValid)
                {
                    builder.UseTexture(
                        farCacheHandle,
                        AccessFlags.Read
                    );
                }


                builder.SetRenderAttachment(
                    starHDR,
                    0,
                    AccessFlags.Write
                );


                builder.SetGlobalTextureAfterPass(
                    starHDR,
                    StarHDRID
                );


                builder.AllowPassCulling(false);


                builder.SetRenderFunc(
                    (
                        StarBufferPassData passData,
                        RasterGraphContext context
                    ) =>
                        ExecuteStarBufferPass(
                            passData,
                            context
                        )
                );
            }


            // ============================================================
            // Bright-star prefilter
            // ============================================================

            TextureHandle brightStars =
                CreateHDRTexture(
                    renderGraph,
                    cameraData.cameraTargetDescriptor,
                    width,
                    height,
                    "Gaia Bright Stars"
                );


            AddBlitPass(
                renderGraph,
                glareMaterial,
                starHDR,
                brightStars,
                1,
                "Gaia Glare Prefilter"
            );


            // ============================================================
            // Glare pyramid
            // ============================================================

            TextureHandle currentSource =
                brightStars;


            for (int level = 0;
                 level < PYRAMID_LEVELS;
                 level++)
            {
                int levelScale =
                    1 << (level + 1);


                int levelWidth =
                    Mathf.Max(
                        1,
                        width / levelScale
                    );


                int levelHeight =
                    Mathf.Max(
                        1,
                        height / levelScale
                    );


                TextureHandle levelTexture =
                    CreateHDRTexture(
                        renderGraph,
                        cameraData.cameraTargetDescriptor,
                        levelWidth,
                        levelHeight,
                        $"Gaia Glare Level {level}"
                    );


                TextureHandle horizontalTexture =
                    CreateHDRTexture(
                        renderGraph,
                        cameraData.cameraTargetDescriptor,
                        levelWidth,
                        levelHeight,
                        $"Gaia Glare Horizontal {level}"
                    );


                AddBlitPass(
                    renderGraph,
                    glareMaterial,
                    currentSource,
                    levelTexture,
                    1,
                    $"Gaia Glare Downsample {level}"
                );


                AddBlitPass(
                    renderGraph,
                    glareMaterial,
                    levelTexture,
                    horizontalTexture,
                    2,
                    $"Gaia Glare Horizontal {level}"
                );


                AddBlitPass(
                    renderGraph,
                    glareMaterial,
                    horizontalTexture,
                    levelTexture,
                    3,
                    $"Gaia Glare Vertical {level}"
                );


                int glareID =
                    level == 0 ? Glare0ID :
                    level == 1 ? Glare1ID :
                    level == 2 ? Glare2ID :
                    level == 3 ? Glare3ID :
                    Glare4ID;


                ExportGlobalTexture(
                    renderGraph,
                    levelTexture,
                    glareID,
                    $"Gaia Export Glare Level {level}"
                );


                currentSource =
                    levelTexture;
            }


            // ============================================================
            // Final composite
            // ============================================================

            TextureHandle finalTarget =
                CreateSameTexture(
                    renderGraph,
                    cameraData.cameraTargetDescriptor,
                    "Gaia Final Color"
                );


            using (
                var builder =
                    renderGraph.AddRasterRenderPass<
                        FinalCompositePassData
                    >(
                        "Gaia Stars Final Composite",
                        out FinalCompositePassData data
                    )
            )
            {
                data.material =
                    finalCompositeMaterial;

                data.cameraColor =
                    colorTarget;

                data.destination =
                    finalTarget;


                builder.UseTexture(
                    data.cameraColor,
                    AccessFlags.Read
                );


                builder.UseGlobalTexture(
                    StarHDRID,
                    AccessFlags.Read
                );

                builder.UseGlobalTexture(
                    Glare0ID,
                    AccessFlags.Read
                );

                builder.UseGlobalTexture(
                    Glare1ID,
                    AccessFlags.Read
                );

                builder.UseGlobalTexture(
                    Glare2ID,
                    AccessFlags.Read
                );

                builder.UseGlobalTexture(
                    Glare3ID,
                    AccessFlags.Read
                );

                builder.UseGlobalTexture(
                    Glare4ID,
                    AccessFlags.Read
                );


                builder.SetRenderAttachment(
                    finalTarget,
                    0,
                    AccessFlags.Write
                );


                builder.AllowPassCulling(false);


                builder.SetRenderFunc(
                    (
                        FinalCompositePassData passData,
                        RasterGraphContext context
                    ) =>
                        ExecuteFinalCompositePass(
                            passData,
                            context
                        )
                );
            }


            resourceData.cameraColor =
                finalTarget;
        }


        // ================================================================
        // External far-cache RTHandle
        // ================================================================

        private void UpdateFarCacheRTHandle(
            RenderTexture texture
        )
        {
            if (texture == null)
            {
                ReleaseFarCacheRTHandle();
                return;
            }


            if (farCacheRTHandle != null &&
                farCacheTextureReference == texture)
            {
                return;
            }


            ReleaseFarCacheRTHandle();


            farCacheRTHandle =
                RTHandles.Alloc(
                    texture
                );

            farCacheTextureReference =
                texture;
        }


        private void ReleaseFarCacheRTHandle()
        {
            if (farCacheRTHandle != null)
            {
                farCacheRTHandle.Release();
                farCacheRTHandle = null;
            }

            farCacheTextureReference =
                null;
        }


        // ================================================================
        // AddBlitPass
        // ================================================================

        private static void AddBlitPass(
            RenderGraph renderGraph,
            Material material,
            TextureHandle source,
            TextureHandle destination,
            int shaderPass,
            string passName
        )
        {
            RenderGraphUtils.BlitMaterialParameters parameters =
                new RenderGraphUtils.BlitMaterialParameters(
                    source,
                    destination,
                    material,
                    shaderPass
                );


            renderGraph.AddBlitPass(
                parameters,
                passName
            );
        }


        // ================================================================
        // Global texture export
        // ================================================================

        private static void ExportGlobalTexture(
            RenderGraph renderGraph,
            TextureHandle texture,
            int textureID,
            string passName
        )
        {
            using (
                var builder =
                    renderGraph.AddRasterRenderPass<
                        GlobalTexturePassData
                    >(
                        passName,
                        out GlobalTexturePassData data
                    )
            )
            {
                builder.SetGlobalTextureAfterPass(
                    texture,
                    textureID
                );


                builder.AllowPassCulling(false);


                builder.SetRenderFunc(
                    static (
                        GlobalTexturePassData passData,
                        RasterGraphContext context
                    ) =>
                    {
                    }
                );
            }
        }


        // ================================================================
        // HDR texture creation
        // ================================================================

        private static TextureHandle CreateHDRTexture(
            RenderGraph renderGraph,
            RenderTextureDescriptor cameraDescriptor,
            int width,
            int height,
            string name
        )
        {
            RenderTextureDescriptor descriptor =
                cameraDescriptor;


            descriptor.width =
                width;

            descriptor.height =
                height;

            descriptor.depthBufferBits =
                0;

            descriptor.msaaSamples =
                1;

            descriptor.graphicsFormat =
                UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;

            descriptor.sRGB =
                false;


            return
                UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    descriptor,
                    name,
                    false
                );
        }


        // ================================================================
        // Final texture
        // ================================================================

        private static TextureHandle CreateSameTexture(
            RenderGraph renderGraph,
            RenderTextureDescriptor cameraDescriptor,
            string name
        )
        {
            RenderTextureDescriptor descriptor =
                cameraDescriptor;


            descriptor.depthBufferBits =
                0;

            descriptor.msaaSamples =
                1;


            return
                UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    descriptor,
                    name,
                    false
                );
        }


        // ================================================================
        // Compute rasterization
        // ================================================================

        private static void ExecuteComputePass(
            ComputePassData data,
            ComputeGraphContext context
        )
        {
            context.cmd.SetComputeBufferParam(
                data.computeShader,
                data.clearKernel,
                StarLuminanceID,
                data.luminance
            );

            context.cmd.SetComputeBufferParam(
                data.computeShader,
                data.clearKernel,
                StarColorLuminanceID,
                data.colorLuminance
            );

            context.cmd.SetComputeIntParam(
                data.computeShader,
                WidthID,
                data.width
            );

            context.cmd.SetComputeIntParam(
                data.computeShader,
                HeightID,
                data.height
            );

            context.cmd.SetComputeIntParam(
                data.computeShader,
                EyeCountID,
                data.eyeCount
            );


            int clearGroupsX =
                Mathf.CeilToInt(
                    data.width / 8.0f
                );

            int clearGroupsY =
                Mathf.CeilToInt(
                    data.height / 8.0f
                );


            context.cmd.DispatchCompute(
                data.computeShader,
                data.clearKernel,
                clearGroupsX,
                clearGroupsY,
                data.eyeCount
            );


            context.cmd.SetComputeFloatParam(
                data.computeShader,
                BrightnessID,
                data.brightnessMultiplier
            );

            context.cmd.SetComputeFloatParam(
                data.computeShader,
                StorageScaleID,
                data.storageScale
            );

            context.cmd.SetComputeIntParam(
                data.computeShader,
                FixedScaleID,
                data.fixedScale
            );

            context.cmd.SetComputeFloatParam(
                data.computeShader,
                ColorMinComputeID,
                data.colorMin
            );

            context.cmd.SetComputeFloatParam(
                data.computeShader,
                ColorMaxComputeID,
                data.colorMax
            );

            context.cmd.SetComputeMatrixParam(
                data.computeShader,
                ViewProj0ID,
                data.viewProj0
            );

            context.cmd.SetComputeMatrixParam(
                data.computeShader,
                ViewProj1ID,
                data.viewProj1
            );


            // ============================================================
            // Live/far separation
            // ============================================================

            context.cmd.SetComputeIntParam(
                data.computeShader,
                UseFarCacheCutoffID,
                data.useFarCacheCutoff ? 1 : 0
            );


            context.cmd.SetComputeVectorParam(
                data.computeShader,
                FarCachePositionID,
                new Vector4(
                    data.farCachePosition.x,
                    data.farCachePosition.y,
                    data.farCachePosition.z,
                    0.0f
                )
            );


            context.cmd.SetComputeFloatParam(
                data.computeShader,
                FarCacheDistanceID,
                data.farCacheDistance
            );


            for (
                int i = 0;
                i < data.starBuffers.Length;
                i++
            )
            {
                BufferHandle starBuffer =
                    data.starBuffers[i];

                int count =
                    data.starCounts[i];


                context.cmd.SetComputeBufferParam(
                    data.computeShader,
                    data.rasterizeKernel,
                    StarsID,
                    starBuffer
                );

                context.cmd.SetComputeBufferParam(
                    data.computeShader,
                    data.rasterizeKernel,
                    StarLuminanceID,
                    data.luminance
                );

                context.cmd.SetComputeBufferParam(
                    data.computeShader,
                    data.rasterizeKernel,
                    StarColorLuminanceID,
                    data.colorLuminance
                );

                context.cmd.SetComputeIntParam(
                    data.computeShader,
                    StarCountID,
                    count
                );

                context.cmd.SetComputeFloatParam(
                    data.computeShader,
                    LODFadeID,
                    data.starFades[i]
                );


                int rasterGroupsX =
                    Mathf.CeilToInt(
                        count / 64.0f
                    );


                context.cmd.DispatchCompute(
                    data.computeShader,
                    data.rasterizeKernel,
                    rasterGroupsX,
                    1,
                    1
                );
            }
        }


        // ================================================================
        // Star buffer → HDR
        // ================================================================

        private static void ExecuteStarBufferPass(
            StarBufferPassData data,
            RasterGraphContext context
        )
        {
            data.material.SetBuffer(
                StarLuminanceID,
                data.luminanceBuffer
            );


            data.material.SetBuffer(
                StarColorLuminanceID,
                data.colorLuminanceBuffer
            );


            data.material.SetFloat(
                StorageScaleInvID,
                data.storageScaleInverse
            );


            data.material.SetFloat(
                FixedScaleID,
                data.fixedScale
            );


            data.material.SetInt(
                WidthID,
                data.width
            );


            data.material.SetInt(
                HeightID,
                data.height
            );


            data.material.SetFloat(
                ColorMinCompositeID,
                data.colorMin
            );


            data.material.SetFloat(
                ColorMaxCompositeID,
                data.colorMax
            );


            // ============================================================
            // Far panorama
            // ============================================================

            data.material.SetFloat(
                FarStarCacheValidID,
                data.farCacheValid
                    ? 1.0f
                    : 0.0f
            );


            data.material.SetFloat(
                FarStarCacheOnlyID,
                data.farCacheOnly
                    ? 1.0f
                    : 0.0f
            );


            if (data.farCacheValid &&
                data.farCacheTexture != null)
            {
                data.material.SetTexture(
                    FarStarCacheID,
                    data.farCacheTexture
                );
            }


            data.material.SetMatrix(
                InvViewProj0ID,
                data.invViewProj0
            );

            data.material.SetMatrix(
                InvViewProj1ID,
                data.invViewProj1
            );


            data.material.SetVector(
                EyePosition0ID,
                data.eyePosition0
            );

            data.material.SetVector(
                EyePosition1ID,
                data.eyePosition1
            );


            CoreUtils.DrawFullScreen(
                context.cmd,
                data.material
            );
        }


        // ================================================================
        // Final composite
        // ================================================================

        private static void ExecuteFinalCompositePass(
            FinalCompositePassData data,
            RasterGraphContext context
        )
        {
            Blitter.BlitTexture(
                context.cmd,
                data.cameraColor,
                new Vector4(
                    1.0f,
                    1.0f,
                    0.0f,
                    0.0f
                ),
                data.material,
                0
            );
        }


        // ================================================================
        // GPU accumulation buffers
        // ================================================================

        private bool EnsureBuffers(
            int width,
            int height,
            int eyeCount
        )
        {
            if (width <= 0 ||
                height <= 0 ||
                eyeCount <= 0)
            {
                return false;
            }


            if (starLuminance != null &&
                starColorLuminance != null &&
                bufferWidth == width &&
                bufferHeight == height &&
                bufferEyeCount == eyeCount)
            {
                return true;
            }


            ReleaseBuffers();


            long pixelCountLong =
                (long)width *
                height *
                eyeCount;


            if (pixelCountLong <= 0 ||
                pixelCountLong > int.MaxValue)
            {
                Debug.LogError(
                    $"GaiaStarRenderPass: Invalid pixel count: {pixelCountLong}"
                );

                return false;
            }


            int pixelCount =
                (int)pixelCountLong;


            try
            {
                starLuminance =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        pixelCount,
                        sizeof(uint)
                    );


                starColorLuminance =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        pixelCount,
                        sizeof(uint)
                    );
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    $"GaiaStarRenderPass: Failed to allocate GPU buffers.\n{exception}"
                );

                ReleaseBuffers();

                return false;
            }


            bufferWidth =
                width;

            bufferHeight =
                height;

            bufferEyeCount =
                eyeCount;


            return true;
        }


        private void ReleaseBuffers()
        {
            if (starLuminance != null)
            {
                starLuminance.Release();
                starLuminance = null;
            }


            if (starColorLuminance != null)
            {
                starColorLuminance.Release();
                starColorLuminance = null;
            }


            bufferWidth = 0;
            bufferHeight = 0;
            bufferEyeCount = 0;
        }


        public void Dispose()
        {
            ReleaseBuffers();

            ReleaseFarCacheRTHandle();


            if (starCompositeMaterial != null)
            {
                CoreUtils.Destroy(
                    starCompositeMaterial
                );

                starCompositeMaterial = null;
            }


            if (glareMaterial != null)
            {
                CoreUtils.Destroy(
                    glareMaterial
                );

                glareMaterial = null;
            }


            if (finalCompositeMaterial != null)
            {
                CoreUtils.Destroy(
                    finalCompositeMaterial
                );

                finalCompositeMaterial = null;
            }
        }


        // ================================================================
        // Pass data
        // ================================================================

        private class StarBufferPassData
        {
            public Material material;

            public GraphicsBuffer luminanceBuffer;
            public GraphicsBuffer colorLuminanceBuffer;

            public TextureHandle output;

            public int width;
            public int height;
            public int eyeCount;

            public float storageScaleInverse;
            public int fixedScale;

            public float colorMin;
            public float colorMax;


            // Far cache
            public RenderTexture farCacheTexture;
            public TextureHandle farCacheHandle;
            public bool farCacheValid;
            public bool farCacheOnly;

            public Matrix4x4 invViewProj0;
            public Matrix4x4 invViewProj1;

            public Vector4 eyePosition0;
            public Vector4 eyePosition1;
        }


        private class FinalCompositePassData
        {
            public Material material;

            public TextureHandle cameraColor;
            public TextureHandle destination;
        }


        private class ComputePassData
        {
            public ComputeShader computeShader;

            public int clearKernel;
            public int rasterizeKernel;

            public BufferHandle luminance;
            public BufferHandle colorLuminance;

            public BufferHandle[] starBuffers;
            public int[] starCounts;
            public float[] starFades;

            public int width;
            public int height;
            public int eyeCount;

            public float brightnessMultiplier;
            public float storageScale;
            public int fixedScale;

            public float colorMin;
            public float colorMax;

            public Matrix4x4 viewProj0;
            public Matrix4x4 viewProj1;


            // Far-cache cutoff
            public bool useFarCacheCutoff;
            public Vector3 farCachePosition;
            public float farCacheDistance;
        }


        private class GlobalTexturePassData
        {
        }
    }
}