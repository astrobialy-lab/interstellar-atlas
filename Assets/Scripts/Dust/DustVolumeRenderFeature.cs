using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Builds a low-resolution cumulative dust optical-depth map for Gaia star extinction.
/// The expensive volume composite is intentionally removed: spatial sprites handle the
/// EnhancedDust display mode, and this feature runs only in RealisticExtinction mode.
/// </summary>
public class DustVolumeRenderFeature : ScriptableRendererFeature
{
    [Header("Star Extinction")]
    [SerializeField] private ComputeShader extinctionComputeShader;

    [SerializeField, Range(16, 128)]
    private int extinctionMaxDimension = 64;

    private DustExtinctionPrecomputePass extinctionPass;

    public override void Create()
    {
        extinctionPass = new DustExtinctionPrecomputePass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingSkybox
        };
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (!Application.isPlaying)
            return;

        if (renderingData.cameraData.cameraType != CameraType.Game ||
            renderingData.cameraData.renderType == CameraRenderType.Overlay)
            return;

        // EnhancedDust doesn't need optical-depth computation. This skips both the
        // compute dispatch and DustLODLoader streaming/uploads until extinction is selected.
        if (!GaiaStarRenderFeature.RealisticExtinctionModeActive)
        {
            DustExtinctionMap.Invalidate();
            return;
        }

        Camera camera = renderingData.cameraData.camera;
        if (camera == null)
            return;

        DustLODLoader loader = UnityEngine.Object.FindAnyObjectByType<DustLODLoader>();
        if (loader == null || extinctionComputeShader == null)
        {
            DustExtinctionMap.Invalidate();
            return;
        }

        extinctionPass.Setup(
            extinctionComputeShader,
            loader,
            camera,
            extinctionMaxDimension);

        renderer.EnqueuePass(extinctionPass);
    }

    protected override void Dispose(bool disposing)
    {
        extinctionPass?.Dispose();
        extinctionPass = null;
    }

    private sealed class DustExtinctionPrecomputePass : ScriptableRenderPass
    {
        private static readonly int OutputID = Shader.PropertyToID("_DustTauOutput");
        private static readonly int OutputWidthID = Shader.PropertyToID("_OutputWidth");
        private static readonly int OutputHeightID = Shader.PropertyToID("_OutputHeight");
        private static readonly int EyeCountID = Shader.PropertyToID("_EyeCount");
        private static readonly int InvViewProj0ID = Shader.PropertyToID("_InvViewProj0");
        private static readonly int InvViewProj1ID = Shader.PropertyToID("_InvViewProj1");
        private static readonly int EyePosition0ID = Shader.PropertyToID("_EyePosition0");
        private static readonly int EyePosition1ID = Shader.PropertyToID("_EyePosition1");

        private ComputeShader computeShader;
        private DustLODLoader loader;
        private Camera camera;
        private int maxDimension = 64;
        private int kernel = -1;

        private RTHandle output;
        private RTHandle brickPoolImportHandle;
        private Texture2DArray importedBrickPoolTexture;
        private int outputWidth;
        private int outputHeight;
        private int outputEyeCount;

        public void Setup(
            ComputeShader compute,
            DustLODLoader dustLoader,
            Camera targetCamera,
            int dimension)
        {
            computeShader = compute;
            loader = dustLoader;
            camera = targetCamera;
            maxDimension = Mathf.Clamp(dimension, 16, 128);
            kernel = computeShader != null
                ? computeShader.FindKernel("BuildDustExtinction")
                : -1;
        }

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData)
        {
            if (!Application.isPlaying ||
                !GaiaStarRenderFeature.RealisticExtinctionModeActive ||
                computeShader == null || loader == null || camera == null || kernel < 0)
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            loader.PrepareForCamera(camera);

            if (loader.PageTableBuffer == null || loader.BrickPoolTexture == null)
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            int fullWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width);
            int fullHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
            int eyeCount = cameraData.xr.enabled && cameraData.xr.viewCount >= 2 ? 2 : 1;

            float scale = Mathf.Min(
                1.0f,
                maxDimension / (float)Mathf.Max(fullWidth, fullHeight));

            int lowWidth = Mathf.Max(1, Mathf.CeilToInt(fullWidth * scale));
            int lowHeight = Mathf.Max(1, Mathf.CeilToInt(fullHeight * scale));

            if (!EnsureOutput(lowWidth, lowHeight, eyeCount))
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            GetCameraMatrices(
                cameraData,
                camera,
                out Matrix4x4 invViewProjLeft,
                out Matrix4x4 invViewProjRight,
                out Vector4 eyePositionLeft,
                out Vector4 eyePositionRight);

            if (importedBrickPoolTexture != loader.BrickPoolTexture)
            {
                if (brickPoolImportHandle != null)
                    RTHandles.Release(brickPoolImportHandle);

                importedBrickPoolTexture = loader.BrickPoolTexture;
                brickPoolImportHandle = importedBrickPoolTexture != null
                    ? RTHandles.Alloc(importedBrickPoolTexture)
                    : null;
            }

            if (brickPoolImportHandle == null)
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            TextureHandle outputHandle = renderGraph.ImportTexture(output);
            TextureHandle brickPoolHandle = renderGraph.ImportTexture(brickPoolImportHandle);
            BufferHandle pageTableHandle = renderGraph.ImportBuffer(loader.PageTableBuffer);

            using (var builder = renderGraph.AddComputePass<ComputePassData>(
                "Dust Cumulative Extinction", out ComputePassData data))
            {
                data.computeShader = computeShader;
                data.kernel = kernel;
                data.loader = loader;
                data.outputHandle = outputHandle;
                data.brickPoolHandle = brickPoolHandle;
                data.pageTableHandle = pageTableHandle;
                data.width = lowWidth;
                data.height = lowHeight;
                data.eyeCount = eyeCount;
                data.invViewProj0 = invViewProjLeft;
                data.invViewProj1 = invViewProjRight;
                data.eyePosition0 = eyePositionLeft;
                data.eyePosition1 = eyePositionRight;

                builder.UseBuffer(pageTableHandle, AccessFlags.Read);
                builder.UseTexture(brickPoolHandle, AccessFlags.Read);
                builder.UseTexture(outputHandle, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(
                    static (ComputePassData passData, ComputeGraphContext context) =>
                        ExecuteComputePass(passData, context));
            }

            DustExtinctionMap.Publish(output, outputHandle, camera);
        }

        private static void GetCameraMatrices(
            UniversalCameraData cameraData,
            Camera camera,
            out Matrix4x4 invViewProjLeft,
            out Matrix4x4 invViewProjRight,
            out Vector4 eyePositionLeft,
            out Vector4 eyePositionRight)
        {
            if (cameraData.xr.enabled && cameraData.xr.viewCount >= 2)
            {
                Matrix4x4 viewLeft = cameraData.xr.GetViewMatrix(0);
                Matrix4x4 projLeft = GL.GetGPUProjectionMatrix(
                    cameraData.xr.GetProjMatrix(0), true);

                Matrix4x4 viewRight = cameraData.xr.GetViewMatrix(1);
                Matrix4x4 projRight = GL.GetGPUProjectionMatrix(
                    cameraData.xr.GetProjMatrix(1), true);

                invViewProjLeft = (projLeft * viewLeft).inverse;
                invViewProjRight = (projRight * viewRight).inverse;

                Vector3 eyeLeft = viewLeft.inverse.GetColumn(3);
                Vector3 eyeRight = viewRight.inverse.GetColumn(3);
                eyePositionLeft = new Vector4(eyeLeft.x, eyeLeft.y, eyeLeft.z, 1f);
                eyePositionRight = new Vector4(eyeRight.x, eyeRight.y, eyeRight.z, 1f);
            }
            else
            {
                Matrix4x4 view = camera.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                invViewProjLeft = (proj * view).inverse;
                invViewProjRight = invViewProjLeft;

                Vector3 eye = camera.transform.position;
                eyePositionLeft = new Vector4(eye.x, eye.y, eye.z, 1f);
                eyePositionRight = eyePositionLeft;
            }
        }

        private bool EnsureOutput(int width, int height, int eyeCount)
        {
            int slices = eyeCount * (DustExtinctionMap.DistanceSampleCount + 1);
            if (output != null && output.rt != null &&
                outputWidth == width && outputHeight == height && outputEyeCount == eyeCount)
                return true;

            ReleaseOutput();

            GraphicsFormat format = GraphicsFormat.R32_SFloat;
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore))
            {
                Debug.LogError(
                    "Dust extinction requires R32_SFloat load/store support.");
                return false;
            }

            try
            {
                output = RTHandles.Alloc(
                    width,
                    height,
                    slices: slices,
                    colorFormat: format,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: TextureDimension.Tex2DArray,
                    enableRandomWrite: true,
                    useMipMap: false,
                    autoGenerateMips: false,
                    name: "Dust Cumulative Optical Depth");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "Failed to allocate the dust extinction texture.\n" + exception);
                ReleaseOutput();
                return false;
            }

            if (output == null || output.rt == null)
            {
                ReleaseOutput();
                return false;
            }

            outputWidth = width;
            outputHeight = height;
            outputEyeCount = eyeCount;
            return true;
        }

        private static void ExecuteComputePass(
            ComputePassData data,
            ComputeGraphContext context)
        {
            if (data.computeShader == null || data.loader == null ||
                !data.outputHandle.IsValid())
                return;

            if (!data.loader.BindExtinctionComputeParameters(
                context,
                data.computeShader,
                data.kernel,
                data.pageTableHandle,
                data.brickPoolHandle))
                return;

            context.cmd.SetComputeTextureParam(
                data.computeShader, data.kernel, OutputID, data.outputHandle);
            context.cmd.SetComputeIntParam(
                data.computeShader, OutputWidthID, data.width);
            context.cmd.SetComputeIntParam(
                data.computeShader, OutputHeightID, data.height);
            context.cmd.SetComputeIntParam(
                data.computeShader, EyeCountID, data.eyeCount);
            context.cmd.SetComputeMatrixParam(
                data.computeShader, InvViewProj0ID, data.invViewProj0);
            context.cmd.SetComputeMatrixParam(
                data.computeShader, InvViewProj1ID, data.invViewProj1);
            context.cmd.SetComputeVectorParam(
                data.computeShader, EyePosition0ID, data.eyePosition0);
            context.cmd.SetComputeVectorParam(
                data.computeShader, EyePosition1ID, data.eyePosition1);

            context.cmd.DispatchCompute(
                data.computeShader,
                data.kernel,
                Mathf.CeilToInt(data.width / 8f),
                Mathf.CeilToInt(data.height / 8f),
                data.eyeCount);
        }

        private void ReleaseOutput()
        {
            if (output != null)
            {
                DustExtinctionMap.Clear(output);
                RTHandles.Release(output);
                output = null;
            }

            outputWidth = 0;
            outputHeight = 0;
            outputEyeCount = 0;
        }

        public void Dispose()
        {
            ReleaseOutput();

            if (brickPoolImportHandle != null)
            {
                RTHandles.Release(brickPoolImportHandle);
                brickPoolImportHandle = null;
            }

            importedBrickPoolTexture = null;
        }

        private sealed class ComputePassData
        {
            public ComputeShader computeShader;
            public int kernel;
            public DustLODLoader loader;
            public TextureHandle outputHandle;
            public TextureHandle brickPoolHandle;
            public BufferHandle pageTableHandle;
            public int width;
            public int height;
            public int eyeCount;
            public Matrix4x4 invViewProj0;
            public Matrix4x4 invViewProj1;
            public Vector4 eyePosition0;
            public Vector4 eyePosition1;
        }
    }
}
