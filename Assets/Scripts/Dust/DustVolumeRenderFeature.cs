using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

public class DustVolumeRenderFeature
    : ScriptableRendererFeature
{
    [Header("Shader")]
    public Shader volumeShader;


    [Header("Render")]
    public RenderPassEvent renderPassEvent =
        RenderPassEvent.AfterRenderingTransparents;


    private Material material;

    private DustVolumePass pass;


    // ============================================================
    // SHADER IDS
    // ============================================================

    private static readonly int InvViewProj0ID =
        Shader.PropertyToID("_InvViewProj0");

    private static readonly int InvViewProj1ID =
        Shader.PropertyToID("_InvViewProj1");

    private static readonly int EyePosition0ID =
        Shader.PropertyToID("_EyePosition0");

    private static readonly int EyePosition1ID =
        Shader.PropertyToID("_EyePosition1");


    // ============================================================
    // CREATE
    // ============================================================

    public override void Create()
    {
        if (
            volumeShader == null
        )
        {
            volumeShader =
                Shader.Find(
                    "Hidden/Dust/Volume"
                );
        }


        if (
            volumeShader != null
        )
        {
            CoreUtils.Destroy(
                material
            );

            material =
                CoreUtils.CreateEngineMaterial(
                    volumeShader
                );
        }


        pass =
            new DustVolumePass();

        pass.renderPassEvent =
            renderPassEvent;
    }


    // ============================================================
    // ADD RENDER PASSES
    // ============================================================

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (
            !Application.isPlaying
        )
        {
            return;
        }


        if (
            material == null
        )
        {
            return;
        }


        DustLODLoader loader =
            Object.FindAnyObjectByType<DustLODLoader>();


        if (
            loader == null
        )
        {
            return;
        }


        if (
            renderingData.cameraData.cameraType
            ==
            CameraType.Preview
        )
        {
            return;
        }


        Camera camera =
            renderingData.cameraData.camera;


        if (
            camera == null
        )
        {
            return;
        }


        pass.Setup(
            material,
            loader,
            camera
        );


        pass.renderPassEvent =
            renderPassEvent;


        pass.ConfigureInput(
            ScriptableRenderPassInput.Depth
        );


        renderer.EnqueuePass(
            pass
        );
    }


    // ============================================================
    // DISPOSE
    // ============================================================

    protected override void Dispose(
        bool disposing)
    {
        pass?.Dispose();

        pass = null;


        CoreUtils.Destroy(
            material
        );

        material = null;
    }


    // ============================================================
    // PASS
    // ============================================================

    private sealed class DustVolumePass
        : ScriptableRenderPass
    {
        private Material material;

        private DustLODLoader loader;

        private Camera camera;


        public void Setup(
            Material material,
            DustLODLoader loader,
            Camera camera)
        {
            this.material =
                material;

            this.loader =
                loader;

            this.camera =
                camera;
        }


        // ========================================================
        // RECORD RENDER GRAPH
        // ========================================================

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameData)
        {
            if (
                !Application.isPlaying
            )
            {
                return;
            }


            if (
                material == null ||
                loader == null ||
                camera == null
            )
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


            if (
                resourceData.isActiveTargetBackBuffer
            )
            {
                return;
            }


            TextureHandle source =
                resourceData.activeColorTexture;


            if (
                !source.IsValid()
            )
            {
                return;
            }


            // ----------------------------------------------------
            // Update dust streaming/GPU resources.
            // ----------------------------------------------------

            loader.PrepareForCamera(
                camera,
                material
            );


            // ====================================================
            // PER-EYE CAMERA MATRICES
            //
            // This is intentionally the same calculation used
            // by GaiaStarRenderFeature.
            // ====================================================

            Matrix4x4 invViewProjLeft;
            Matrix4x4 invViewProjRight;

            Vector4 eyePositionLeft;
            Vector4 eyePositionRight;


            if (
                cameraData.xr.enabled &&
                cameraData.xr.viewCount >= 2
            )
            {
                // ------------------------------------------------
                // Left eye
                // ------------------------------------------------

                Matrix4x4 viewLeft =
                    cameraData.xr.GetViewMatrix(
                        0
                    );


                Matrix4x4 projLeft =
                    GL.GetGPUProjectionMatrix(
                        cameraData.xr.GetProjMatrix(
                            0
                        ),
                        true
                    );


                Matrix4x4 viewProjLeft =
                    projLeft *
                    viewLeft;


                invViewProjLeft =
                    viewProjLeft.inverse;


                // ------------------------------------------------
                // Right eye
                // ------------------------------------------------

                Matrix4x4 viewRight =
                    cameraData.xr.GetViewMatrix(
                        1
                    );


                Matrix4x4 projRight =
                    GL.GetGPUProjectionMatrix(
                        cameraData.xr.GetProjMatrix(
                            1
                        ),
                        true
                    );


                Matrix4x4 viewProjRight =
                    projRight *
                    viewRight;


                invViewProjRight =
                    viewProjRight.inverse;


                // ------------------------------------------------
                // Actual eye world positions
                // ------------------------------------------------

                Matrix4x4 eyeWorldLeft =
                    viewLeft.inverse;


                Matrix4x4 eyeWorldRight =
                    viewRight.inverse;


                Vector3 eyeLeft =
                    eyeWorldLeft.GetColumn(
                        3
                    );


                Vector3 eyeRight =
                    eyeWorldRight.GetColumn(
                        3
                    );


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
                // ------------------------------------------------
                // Non-XR
                // ------------------------------------------------

                Matrix4x4 view =
                    camera.worldToCameraMatrix;


                Matrix4x4 proj =
                    GL.GetGPUProjectionMatrix(
                        camera.projectionMatrix,
                        true
                    );


                Matrix4x4 viewProj =
                    proj *
                    view;


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


            // ====================================================
            // PASS CAMERA DATA TO DUST SHADER
            // ====================================================

            material.SetMatrix(
                InvViewProj0ID,
                invViewProjLeft
            );


            material.SetMatrix(
                InvViewProj1ID,
                invViewProjRight
            );


            material.SetVector(
                EyePosition0ID,
                eyePositionLeft
            );


            material.SetVector(
                EyePosition1ID,
                eyePositionRight
            );


            // ====================================================
            // DESTINATION
            // ====================================================

            TextureDesc destinationDesc =
                renderGraph.GetTextureDesc(
                    source
                );


            destinationDesc.name =
                "DustVolumeColor";


            destinationDesc.clearBuffer =
                false;


            destinationDesc.depthBufferBits =
                0;


            TextureHandle destination =
                renderGraph.CreateTexture(
                    destinationDesc
                );


            // ====================================================
            // BLIT
            // ====================================================

            RenderGraphUtils.BlitMaterialParameters
                parameters =
                    new RenderGraphUtils
                        .BlitMaterialParameters(
                            source,
                            destination,
                            material,
                            0
                        );


            renderGraph.AddBlitPass(
                parameters,
                "Dust Volume"
            );


            // ====================================================
            // NEW CAMERA COLOR
            // ====================================================

            resourceData.cameraColor =
                destination;
        }


        public void Dispose()
        {
            material = null;
            loader = null;
            camera = null;
        }
    }
}