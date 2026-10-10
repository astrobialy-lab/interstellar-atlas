using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

public class DustVolumeRenderFeature : ScriptableRendererFeature
{
    [Header("Shader")]
    public Shader volumeShader;

    [Header("Render")]
    public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

    [Header("Star Extinction Precompute")]
    [Tooltip("Assign DustExtinctionPrecompute.compute. It builds a low-resolution cumulative optical-depth map for stars.")]
    public ComputeShader extinctionComputeShader;

    [SerializeField, Range(2, 16)]
    private int extinctionResolutionDivisor = 8;

    private Material material;
    private DustVolumePass pass;
    private DustExtinctionPrecomputePass extinctionPass;

    private static readonly int InvViewProj0ID = Shader.PropertyToID("_InvViewProj0");
    private static readonly int InvViewProj1ID = Shader.PropertyToID("_InvViewProj1");
    private static readonly int EyePosition0ID = Shader.PropertyToID("_EyePosition0");
    private static readonly int EyePosition1ID = Shader.PropertyToID("_EyePosition1");

    public override void Create()
    {
        if (volumeShader == null)
            volumeShader = Shader.Find("Hidden/Dust/Volume");

        if (volumeShader != null)
        {
            CoreUtils.Destroy(material);
            material = CoreUtils.CreateEngineMaterial(volumeShader);
        }

        pass = new DustVolumePass();
        pass.renderPassEvent = renderPassEvent;

        extinctionPass = new DustExtinctionPrecomputePass();
        extinctionPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!Application.isPlaying || material == null)
            return;

        DustLODLoader loader = UnityEngine.Object.FindAnyObjectByType<DustLODLoader>();
        if (loader == null)
            return;

        if (renderingData.cameraData.cameraType == CameraType.Preview)
            return;

        Camera camera = renderingData.cameraData.camera;
        if (camera == null)
            return;

        bool isGaiaGameCamera =
            renderingData.cameraData.cameraType == CameraType.Game &&
            renderingData.cameraData.renderType != CameraRenderType.Overlay;

        if (extinctionComputeShader != null && isGaiaGameCamera)
        {
            extinctionPass.Setup(
                extinctionComputeShader,
                material,
                loader,
                camera,
                extinctionResolutionDivisor);
            renderer.EnqueuePass(extinctionPass);
        }
        else if (isGaiaGameCamera)
        {
            DustExtinctionMap.Invalidate();
        }

        pass.Setup(material, loader, camera);

        // The depth-aware star attenuation is applied while Gaia stars are rasterized.
        // Keep the volume composite before those stars so it doesn't attenuate them twice.
        pass.renderPassEvent = extinctionComputeShader != null
            ? RenderPassEvent.BeforeRenderingTransparents
            : renderPassEvent;

        pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        extinctionPass?.Dispose();
        pass = null;
        extinctionPass = null;
        CoreUtils.Destroy(material);
        material = null;
    }

    // ============================================================
    // Low-resolution cumulative extinction precompute
    // ============================================================

    private sealed class DustExtinctionPrecomputePass : ScriptableRenderPass
    {
        private static readonly int OutputID = Shader.PropertyToID("_DustTauOutput");
        private static readonly int OutputWidthID = Shader.PropertyToID("_OutputWidth");
        private static readonly int OutputHeightID = Shader.PropertyToID("_OutputHeight");
        private static readonly int EyeCountID = Shader.PropertyToID("_EyeCount");
        private static readonly int DistanceSampleCountID = Shader.PropertyToID("_DistanceSampleCount");
        private static readonly int InvViewProj0ID = Shader.PropertyToID("_InvViewProj0");
        private static readonly int InvViewProj1ID = Shader.PropertyToID("_InvViewProj1");
        private static readonly int EyePosition0ID = Shader.PropertyToID("_EyePosition0");
        private static readonly int EyePosition1ID = Shader.PropertyToID("_EyePosition1");

        private ComputeShader computeShader;
        private Material dustMaterial;
        private DustLODLoader loader;
        private Camera camera;
        private int resolutionDivisor = 8;
        private int kernel = -1;

        private RTHandle output;
        private RTHandle brickPoolImportHandle;
        private Texture2DArray importedBrickPoolTexture;
        private int outputWidth;
        private int outputHeight;
        private int outputEyeCount;

        public void Setup(ComputeShader compute, Material material, DustLODLoader dustLoader, Camera targetCamera, int divisor)
        {
            computeShader = compute;
            dustMaterial = material;
            loader = dustLoader;
            camera = targetCamera;
            resolutionDivisor = Mathf.Clamp(divisor, 2, 16);
            kernel = computeShader != null ? computeShader.FindKernel("BuildDustExtinction") : -1;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!Application.isPlaying || computeShader == null || dustMaterial == null || loader == null || camera == null || kernel < 0)
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            loader.PrepareForCamera(camera, dustMaterial);

            if (loader.PageTableBuffer == null || loader.BrickPoolTexture == null)
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            int fullWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width);
            int fullHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
            int eyeCount = cameraData.xr.enabled && cameraData.xr.viewCount >= 2 ? 2 : 1;
            int lowWidth = Mathf.Max(1, (fullWidth + resolutionDivisor - 1) / resolutionDivisor);
            int lowHeight = Mathf.Max(1, (fullHeight + resolutionDivisor - 1) / resolutionDivisor);

            if (!EnsureOutput(lowWidth, lowHeight, eyeCount))
            {
                DustExtinctionMap.Invalidate();
                return;
            }

            Matrix4x4 invViewProjLeft;
            Matrix4x4 invViewProjRight;
            Vector4 eyePositionLeft;
            Vector4 eyePositionRight;

            if (cameraData.xr.enabled && cameraData.xr.viewCount >= 2)
            {
                Matrix4x4 viewLeft = cameraData.xr.GetViewMatrix(0);
                Matrix4x4 projLeft = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(0), true);
                invViewProjLeft = (projLeft * viewLeft).inverse;

                Matrix4x4 viewRight = cameraData.xr.GetViewMatrix(1);
                Matrix4x4 projRight = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(1), true);
                invViewProjRight = (projRight * viewRight).inverse;

                Vector3 eyeLeft = viewLeft.inverse.GetColumn(3);
                Vector3 eyeRight = viewRight.inverse.GetColumn(3);
                eyePositionLeft = new Vector4(eyeLeft.x, eyeLeft.y, eyeLeft.z, 1.0f);
                eyePositionRight = new Vector4(eyeRight.x, eyeRight.y, eyeRight.z, 1.0f);
            }
            else
            {
                Matrix4x4 view = camera.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                invViewProjLeft = (proj * view).inverse;
                invViewProjRight = invViewProjLeft;
                Vector3 eye = camera.transform.position;
                eyePositionLeft = new Vector4(eye.x, eye.y, eye.z, 1.0f);
                eyePositionRight = eyePositionLeft;
            }

            if (importedBrickPoolTexture != loader.BrickPoolTexture)
            {
                if (brickPoolImportHandle != null)
                    RTHandles.Release(brickPoolImportHandle);
                brickPoolImportHandle = loader.BrickPoolTexture != null
                    ? RTHandles.Alloc(loader.BrickPoolTexture)
                    : null;
                importedBrickPoolTexture = loader.BrickPoolTexture;
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
                "Dust Cumulative Extinction (Low Resolution)", out ComputePassData data))
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
                builder.SetRenderFunc((ComputePassData passData, ComputeGraphContext context) =>
                    ExecuteComputePass(passData, context));
            }

            DustExtinctionMap.Publish(output, outputHandle, camera);
        }

        private bool EnsureOutput(int width, int height, int eyeCount)
        {
            int slices = eyeCount * (DustExtinctionMap.DistanceSampleCount + 1);
            if (output != null && output.rt != null &&
                outputWidth == width && outputHeight == height && outputEyeCount == eyeCount)
                return true;

            ReleaseOutput();

            // Use a single-channel R32F texture for broad random-write compatibility.
            // Per eye: slice 0 stores first dust distance; slices 1..N store cumulative tau.
            // This avoids RG16F/RG32F image-store format issues on some GLES drivers.
            GraphicsFormat format = GraphicsFormat.R32_SFloat;
            if (!SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore))
            {
                Debug.LogError("Dust extinction precompute: this GPU does not support R32_SFloat random-write textures.");
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
                Debug.LogError("Dust extinction precompute: failed to allocate the lookup texture.\n" + exception);
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

        private static void ExecuteComputePass(ComputePassData data, ComputeGraphContext context)
        {
            if (data.computeShader == null || data.loader == null || !data.outputHandle.IsValid())
                return;

            if (!data.loader.BindExtinctionComputeParameters(
                context, data.computeShader, data.kernel, data.pageTableHandle, data.brickPoolHandle))
                return;

            context.cmd.SetComputeTextureParam(data.computeShader, data.kernel, OutputID, data.outputHandle);
            context.cmd.SetComputeIntParam(data.computeShader, OutputWidthID, data.width);
            context.cmd.SetComputeIntParam(data.computeShader, OutputHeightID, data.height);
            context.cmd.SetComputeIntParam(data.computeShader, EyeCountID, data.eyeCount);
            context.cmd.SetComputeIntParam(data.computeShader, DistanceSampleCountID, DustExtinctionMap.DistanceSampleCount);
            context.cmd.SetComputeMatrixParam(data.computeShader, InvViewProj0ID, data.invViewProj0);
            context.cmd.SetComputeMatrixParam(data.computeShader, InvViewProj1ID, data.invViewProj1);
            context.cmd.SetComputeVectorParam(data.computeShader, EyePosition0ID, data.eyePosition0);
            context.cmd.SetComputeVectorParam(data.computeShader, EyePosition1ID, data.eyePosition1);

            int groupsX = Mathf.CeilToInt(data.width / 8.0f);
            int groupsY = Mathf.CeilToInt(data.height / 8.0f);
            context.cmd.DispatchCompute(data.computeShader, data.kernel, groupsX, groupsY, data.eyeCount);
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

    // ============================================================
    // Existing volume pass
    // ============================================================

    private sealed class DustVolumePass : ScriptableRenderPass
    {
        private Material material;
        private DustLODLoader loader;
        private Camera camera;

        public void Setup(Material material, DustLODLoader loader, Camera camera)
        {
            this.material = material;
            this.loader = loader;
            this.camera = camera;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!Application.isPlaying || material == null || loader == null || camera == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.activeColorTexture;
            if (!source.IsValid())
                return;

            loader.PrepareForCamera(camera, material);

            Matrix4x4 invViewProjLeft;
            Matrix4x4 invViewProjRight;
            Vector4 eyePositionLeft;
            Vector4 eyePositionRight;

            if (cameraData.xr.enabled && cameraData.xr.viewCount >= 2)
            {
                Matrix4x4 viewLeft = cameraData.xr.GetViewMatrix(0);
                Matrix4x4 projLeft = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(0), true);
                invViewProjLeft = (projLeft * viewLeft).inverse;

                Matrix4x4 viewRight = cameraData.xr.GetViewMatrix(1);
                Matrix4x4 projRight = GL.GetGPUProjectionMatrix(cameraData.xr.GetProjMatrix(1), true);
                invViewProjRight = (projRight * viewRight).inverse;

                Vector3 eyeLeft = viewLeft.inverse.GetColumn(3);
                Vector3 eyeRight = viewRight.inverse.GetColumn(3);
                eyePositionLeft = new Vector4(eyeLeft.x, eyeLeft.y, eyeLeft.z, 1.0f);
                eyePositionRight = new Vector4(eyeRight.x, eyeRight.y, eyeRight.z, 1.0f);
            }
            else
            {
                Matrix4x4 view = camera.worldToCameraMatrix;
                Matrix4x4 proj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                invViewProjLeft = (proj * view).inverse;
                invViewProjRight = invViewProjLeft;
                Vector3 eye = camera.transform.position;
                eyePositionLeft = new Vector4(eye.x, eye.y, eye.z, 1.0f);
                eyePositionRight = eyePositionLeft;
            }

            material.SetMatrix(InvViewProj0ID, invViewProjLeft);
            material.SetMatrix(InvViewProj1ID, invViewProjRight);
            material.SetVector(EyePosition0ID, eyePositionLeft);
            material.SetVector(EyePosition1ID, eyePositionRight);

            TextureDesc destinationDesc = renderGraph.GetTextureDesc(source);
            destinationDesc.name = "DustVolumeColor";
            destinationDesc.clearBuffer = false;
            destinationDesc.depthBufferBits = 0;
            TextureHandle destination = renderGraph.CreateTexture(destinationDesc);

            RenderGraphUtils.BlitMaterialParameters parameters =
                new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0);
            renderGraph.AddBlitPass(parameters, "Dust Volume");
            resourceData.cameraColor = destination;
        }

        public void Dispose()
        {
            material = null;
            loader = null;
            camera = null;
        }
    }
}
