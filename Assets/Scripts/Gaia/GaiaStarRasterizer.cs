using System.Collections.Generic;
using UnityEngine;

public class GaiaStarRasterizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField]
    private ComputeShader rasterizerCompute;

    [SerializeField]
    private GaiaStarBufferProvider starLoader;

    [Header("Brightness")]
    [SerializeField]
    private float brightnessMultiplier = 100.0f;

    [Header("Storage")]
    [SerializeField]
    private float storageScale = 1.0f / 1024.0f;

    [Header("Colour")]
    [SerializeField]
    private float colorMin = -0.5f;

    [SerializeField]
    private float colorMax = 3.5f;

    private const int FIXED_SCALE = 65536;

    private int clearKernel;
    private int rasterizeKernel;

    public ComputeShader RasterizerCompute
    {
        get
        {
            return rasterizerCompute;
        }
    }

    public GaiaStarBufferProvider StarLoader
    {
        get
        {
            return starLoader;
        }
    }

    public int ClearKernel
    {
        get
        {
            return clearKernel;
        }
    }

    public int RasterizeKernel
    {
        get
        {
            return rasterizeKernel;
        }
    }

    public float BrightnessMultiplier
    {
        get
        {
            return brightnessMultiplier;
        }
    }

    public float StorageScale
    {
        get
        {
            return storageScale;
        }
    }

    public float StorageScaleInverse
    {
        get
        {
            return 1.0f / storageScale;
        }
    }

    public float ColorMin
    {
        get
        {
            return colorMin;
        }
    }

    public float ColorMax
    {
        get
        {
            return colorMax;
        }
    }

    public int FixedScale
    {
        get
        {
            return FIXED_SCALE;
        }
    }

    private void Awake()
    {
        if (rasterizerCompute == null)
        {
            Debug.LogError(
                "GaiaStarRasterizer: No ComputeShader assigned."
            );

            enabled = false;
            return;
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError(
                "GaiaStarRasterizer: This platform does not support compute shaders."
            );

            enabled = false;
            return;
        }

        clearKernel =
            rasterizerCompute.FindKernel("ClearKernel");

        rasterizeKernel =
            rasterizerCompute.FindKernel("RasterizeKernel");

        if (starLoader == null)
        {
            starLoader =
                GetComponent<GaiaStarBufferProvider>();
        }

        if (starLoader == null)
        {
            Debug.LogError(
                "GaiaStarRasterizer: No GaiaStarLoader found."
            );

            enabled = false;
        }
    }

    public void GetLoadedStarBuffers(
        List<GraphicsBuffer> buffers,
        List<int> counts,
        List<float> fades)
    {
        if (starLoader == null)
        {
            buffers.Clear();
            counts.Clear();
            fades.Clear();
            return;
        }

        starLoader.GetLoadedStarBuffers(
            buffers,
            counts,
            fades
        );
    }
}