using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>
/// Shared handle for the low-resolution cumulative dust optical-depth texture.
/// Written before Gaia's compute rasterizer reads it in the same frame.
/// </summary>
public static class DustExtinctionMap
{
    // Samples: 0, 1, 2, 4, 8, ... 4096 parsecs.
    public const int DistanceSampleCount = 14;

    private static RTHandle currentHandle;
    private static int scheduledFrame = -1;
    private static Camera scheduledCamera;

    // Compute shaders require every declared resource to be bound, even when
    // _DustExtinctionValid is zero and the shader will not sample the resource.
    // This tiny neutral Texture2DArray is the binding fallback when the real map
    // has not been produced for the current camera/frame.
    private static Texture2DArray fallbackTexture;
    private static RTHandle fallbackHandle;

    public static RTHandle Handle => currentHandle;
    public static TextureHandle GraphTexture { get; private set; }

    public static RTHandle GetFallbackHandle()
    {
        if (fallbackHandle != null)
            return fallbackHandle;

        fallbackTexture = new Texture2DArray(
            1, 1, 1, TextureFormat.RHalf, false, true)
        {
            name = "DustExtinctionNeutralFallback",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        fallbackTexture.SetPixels(new[] { Color.black }, 0, 0);
        fallbackTexture.Apply(false, false);
        fallbackHandle = RTHandles.Alloc(fallbackTexture);
        return fallbackHandle;
    }

    public static void ReleaseFallback()
    {
        if (fallbackHandle != null)
        {
            RTHandles.Release(fallbackHandle);
            fallbackHandle = null;
        }

        if (fallbackTexture != null)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(fallbackTexture);
            else
                UnityEngine.Object.DestroyImmediate(fallbackTexture);
            fallbackTexture = null;
        }
    }

    public static bool IsAvailableForCurrentFrame(Camera camera) =>
        camera != null &&
        currentHandle != null &&
        currentHandle.rt != null &&
        scheduledFrame == Time.frameCount &&
        scheduledCamera == camera;

    public static void Publish(RTHandle handle, TextureHandle graphTexture, Camera camera)
    {
        currentHandle = handle;
        GraphTexture = graphTexture;
        scheduledFrame = handle != null ? Time.frameCount : -1;
        scheduledCamera = handle != null ? camera : null;
    }

    public static void Invalidate()
    {
        scheduledFrame = -1;
        scheduledCamera = null;
        GraphTexture = default;
    }

    public static void Clear(RTHandle handle)
    {
        if (currentHandle == handle)
        {
            currentHandle = null;
            GraphTexture = default;
            scheduledFrame = -1;
            scheduledCamera = null;
        }
    }
}
