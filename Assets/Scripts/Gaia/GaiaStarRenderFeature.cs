using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class GaiaStarRenderFeature :
    ScriptableRendererFeature
{
    [SerializeField]
    private Shader compositeShader;

    [SerializeField]
    private GaiaStarRasterizer rasterizer;

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

        renderPass =
            new GaiaStarRenderPass(
                compositeShader,
                rasterizer
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
        private Material compositeMaterial;

        private GaiaStarRasterizer rasterizer;

        private GraphicsBuffer starLuminance;
        private GraphicsBuffer starColorLuminance;

        private int bufferWidth;
        private int bufferHeight;
        private int bufferEyeCount;

        private readonly List<GraphicsBuffer> starBuffers =
            new List<GraphicsBuffer>();

        private readonly List<int> starCounts =
            new List<int>();

        private readonly List<BufferHandle> starBufferHandles =
            new List<BufferHandle>();

        private readonly List<GraphicsBuffer> starBufferArray =
            new List<GraphicsBuffer>();

        private readonly List<int> starCountArray =
            new List<int>();

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

        public GaiaStarRenderPass(
            Shader shader,
            GaiaStarRasterizer rasterizer
        )
        {
            this.rasterizer =
                rasterizer;

            if (shader != null)
            {
                compositeMaterial =
                    CoreUtils.CreateEngineMaterial(
                        shader
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

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData
        )
        {
            if (rasterizer == null ||
                !rasterizer.enabled ||
                rasterizer.RasterizerCompute == null ||
                rasterizer.StarLoader == null ||
                compositeMaterial == null)
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

            starBuffers.Clear();
            starCounts.Clear();

            rasterizer.GetLoadedStarBuffers(
                starBuffers,
                starCounts
            );

            starBufferHandles.Clear();
            starBufferArray.Clear();
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

                starBufferArray.Add(
                    buffer
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

            Matrix4x4 view =
                camera.worldToCameraMatrix;

            Matrix4x4 proj =
                GL.GetGPUProjectionMatrix(
                    camera.projectionMatrix,
                    true
                );

            Matrix4x4 viewProj =
                proj * view;

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
                    viewProj;

                computeData.viewProj1 =
                    viewProj;

                builder.UseBuffer(
                    computeData.luminance,
                    AccessFlags.Write
                );

                builder.UseBuffer(
                    computeData.colorLuminance,
                    AccessFlags.Write
                );

                for (int i = 0;
                     i < computeData.starBuffers.Length;
                     i++)
                {
                    builder.UseBuffer(
                        computeData.starBuffers[i],
                        AccessFlags.Read
                    );
                }

                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (ComputePassData data, ComputeGraphContext context) =>
                        ExecuteComputePass(data, context)
                );
            }

            using (
                var builder =
                    renderGraph.AddRasterRenderPass<
                        CompositePassData
                    >(
                        "Gaia Star Composite",
                        out CompositePassData compositeData
                    )
            )
            {
                compositeData.compositeMaterial =
                    compositeMaterial;

                compositeData.luminanceBuffer =
                    starLuminance;

                compositeData.colorLuminanceBuffer =
                    starColorLuminance;

                compositeData.luminanceHandle =
                    luminanceHandle;

                compositeData.colorLuminanceHandle =
                    colorLuminanceHandle;

                compositeData.width =
                    width;

                compositeData.height =
                    height;

                compositeData.storageScaleInverse =
                    rasterizer.StorageScaleInverse;

                compositeData.fixedScale =
                    rasterizer.FixedScale;

                compositeData.colorMin =
                    rasterizer.ColorMin;

                compositeData.colorMax =
                    rasterizer.ColorMax;

                builder.UseBuffer(
                    compositeData.luminanceHandle,
                    AccessFlags.Read
                );

                builder.UseBuffer(
                    compositeData.colorLuminanceHandle,
                    AccessFlags.Read
                );

                builder.SetRenderAttachment(
                    colorTarget,
                    0,
                    AccessFlags.Write
                );

                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    (CompositePassData data, RasterGraphContext context) =>
                        ExecuteCompositePass(data, context)
                );
            }
        }

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

            for (int i = 0;
                 i < data.starBuffers.Length;
                 i++)
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

                int rasterGroupsX =
                    Mathf.CeilToInt(
                        count / 8.0f
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

        private static void ExecuteCompositePass(
            CompositePassData data,
            RasterGraphContext context
        )
        {
            data.compositeMaterial.SetBuffer(
                StarLuminanceID,
                data.luminanceBuffer
            );

            data.compositeMaterial.SetBuffer(
                StarColorLuminanceID,
                data.colorLuminanceBuffer
            );

            data.compositeMaterial.SetFloat(
                StorageScaleInvID,
                data.storageScaleInverse
            );

            data.compositeMaterial.SetFloat(
                FixedScaleID,
                data.fixedScale
            );

            data.compositeMaterial.SetInt(
                WidthID,
                data.width
            );

            data.compositeMaterial.SetInt(
                HeightID,
                data.height
            );

            data.compositeMaterial.SetFloat(
                ColorMinCompositeID,
                data.colorMin
            );

            data.compositeMaterial.SetFloat(
                ColorMaxCompositeID,
                data.colorMax
            );

            CoreUtils.DrawFullScreen(
                context.cmd,
                data.compositeMaterial
            );
        }

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

            if (compositeMaterial != null)
            {
                CoreUtils.Destroy(
                    compositeMaterial
                );

                compositeMaterial = null;
            }
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
        }

        private class CompositePassData
        {
            public Material compositeMaterial;

            public GraphicsBuffer luminanceBuffer;
            public GraphicsBuffer colorLuminanceBuffer;

            public BufferHandle luminanceHandle;
            public BufferHandle colorLuminanceHandle;

            public int width;
            public int height;

            public float storageScaleInverse;
            public int fixedScale;

            public float colorMin;
            public float colorMax;
        }
    }
}