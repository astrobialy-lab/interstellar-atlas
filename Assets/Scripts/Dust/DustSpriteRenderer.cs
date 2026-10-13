using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws an offline-generated set of spatial dust billboards with one GPU-instanced draw.
/// This is an artistic appearance layer; the separate extinction compute pass handles
/// attenuation and reddening of Gaia stars.
/// </summary>
[DisallowMultipleComponent]
public sealed class DustSpriteRenderer : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private string relativeDataPath = "Data/Dust/dust_sprites.bin";

    [Header("Rendering")]
    [SerializeField] private Shader spriteShader;
    [SerializeField] private Color spriteTint = new Color(0.60f, 0.48f, 0.36f, 1f);
    [SerializeField, Range(0f, 3f)] private float opacity = 1.25f;
    [SerializeField] private Camera targetCamera;

    private const string FileMagic = "DUSTSPR1";
    private const uint FileVersion = 1;
    private const int MaxAcceptedSprites = 100000;
    private const int SpriteStrideBytes = 32;
    private const int CloudTextureSize = 128;

    private static readonly int SpriteBufferId = Shader.PropertyToID("_DustSprites");
    private static readonly int CloudTextureId = Shader.PropertyToID("_CloudTexture");
    private static readonly int TintId = Shader.PropertyToID("_DustTint");
    private static readonly int OpacityId = Shader.PropertyToID("_GlobalOpacity");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpriteGpuData
    {
        public Vector4 positionSize; // xyz = world position in pc; w = diameter in pc
        public Vector4 parameters;   // x = opacity, y = rotation radians, z = aspect ratio, w = normalized density
    }

    private GraphicsBuffer spriteBuffer;
    private Material spriteMaterial;
    private Mesh quadMesh;
    private Texture2D cloudTexture;
    private MaterialPropertyBlock properties;
    private Bounds worldBounds;
    private int spriteCount;
    private bool enhancedModeEnabled = true;
    private bool loadAttempted;
    private bool drawLogPrinted;
    private bool missingCameraWarningPrinted;
    private bool layerWarningPrinted;

    public int SpriteCount => spriteCount;

    /// <summary>Called by GaiaStarRenderFeature when its display mode changes.</summary>
    public void SetEnhancedMode(bool enabled)
    {
        enhancedModeEnabled = enabled;
    }

    /// <summary>Passes the camera currently being rendered by the star feature.</summary>
    public void SetTargetCamera(Camera camera)
    {
        targetCamera = camera;
    }

    private void Awake()
    {
        if (spriteShader == null)
            spriteShader = Shader.Find("Hidden/Dust/SpatialSprites");

        if (spriteShader == null)
        {
            Debug.LogError("DustSpriteRenderer: Shader 'Hidden/Dust/SpatialSprites' was not found.", this);
            enabled = false;
            return;
        }

        if (!SystemInfo.supportsInstancing)
        {
            Debug.LogError("DustSpriteRenderer: GPU instancing is not supported on this device.", this);
            enabled = false;
            return;
        }

        LoadSprites();
    }

    private void LateUpdate()
    {
        if (!enhancedModeEnabled || spriteCount == 0 || spriteBuffer == null || spriteMaterial == null || quadMesh == null)
            return;

        Camera cameraToUse = targetCamera != null ? targetCamera : Camera.main;
        if (cameraToUse == null)
        {
            if (!missingCameraWarningPrinted)
            {
                Debug.LogWarning("DustSpriteRenderer: No target camera assigned and Camera.main was not found. Assign the XR Main Camera to Target Camera.", this);
                missingCameraWarningPrinted = true;
            }
            return;
        }

        // RenderParams.layer participates in camera culling. The Dust object's layer might
        // not be enabled on the XR camera, so fall back to Default when possible.
        int renderLayer = gameObject.layer;
        if ((cameraToUse.cullingMask & (1 << renderLayer)) == 0)
        {
            if ((cameraToUse.cullingMask & 1) != 0)
            {
                renderLayer = 0;
            }
            else if (!layerWarningPrinted)
            {
                Debug.LogWarning("DustSpriteRenderer: The camera culling mask excludes both the Dust object's layer and Default. Sprite draw may be culled.", this);
                layerWarningPrinted = true;
            }
        }

        properties.SetColor(TintId, spriteTint);
        properties.SetFloat(OpacityId, opacity);

        RenderParams renderParams = new RenderParams(spriteMaterial)
        {
            camera = cameraToUse,
            worldBounds = worldBounds,
            matProps = properties,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            layer = renderLayer
        };

        Graphics.RenderMeshPrimitives(renderParams, quadMesh, 0, spriteCount);

        if (!drawLogPrinted)
        {
            Debug.Log(
                $"DustSpriteRenderer: submitted {spriteCount:N0} sprites to camera '{cameraToUse.name}'. " +
                $"Bounds center={worldBounds.center}, size={worldBounds.size}, " +
                $"camera position={cameraToUse.transform.position}, far clip={cameraToUse.farClipPlane}, layer={renderLayer}.", this);
            drawLogPrinted = true;
        }
    }

    private void LoadSprites()
    {
        if (loadAttempted)
            return;
        loadAttempted = true;

        try
        {
            string path = ResolveDataPath(relativeDataPath);
            if (!File.Exists(path))
            {
                Debug.LogError(
                    $"DustSpriteRenderer: Sprite file not found:\n{path}\n" +
                    "Run 4_build_dust_sprites.py on the development PC and include the generated file in the app data package.", this);
                return;
            }

            SpriteGpuData[] sprites;
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            float largestSize = 0f;

            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                string magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (magic != FileMagic)
                    throw new InvalidDataException($"Unexpected sprite file magic '{magic}'.");

                uint version = reader.ReadUInt32();
                if (version != FileVersion)
                    throw new InvalidDataException($"Unsupported sprite file version {version}.");

                uint count = reader.ReadUInt32();
                if (count == 0 || count > MaxAcceptedSprites)
                    throw new InvalidDataException($"Sprite count {count} is outside the supported range 1..{MaxAcceptedSprites}.");

                long expectedLength = 16L + (long)count * SpriteStrideBytes;
                if (stream.Length != expectedLength)
                    throw new InvalidDataException($"Invalid sprite file length. Expected {expectedLength} bytes; found {stream.Length}.");

                sprites = new SpriteGpuData[count];
                for (int i = 0; i < sprites.Length; i++)
                {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    float size = reader.ReadSingle();
                    float spriteOpacity = reader.ReadSingle();
                    float rotation = reader.ReadSingle();
                    float aspect = reader.ReadSingle();
                    float normalizedDensity = reader.ReadSingle();

                    if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) ||
                        !IsFinite(size) || !IsFinite(spriteOpacity) ||
                        !IsFinite(rotation) || !IsFinite(aspect) ||
                        !IsFinite(normalizedDensity) || size <= 0f || aspect <= 0f)
                    {
                        throw new InvalidDataException($"Invalid values in sprite record {i}.");
                    }

                    sprites[i] = new SpriteGpuData
                    {
                        positionSize = new Vector4(x, y, z, size),
                        parameters = new Vector4(
                            Mathf.Clamp01(spriteOpacity), rotation,
                            Mathf.Clamp(aspect, 0.25f, 4f),
                            Mathf.Clamp01(normalizedDensity))
                    };

                    Vector3 center = new Vector3(x, y, z);
                    float radius = size * Mathf.Max(1f, aspect) * 0.75f;
                    min = Vector3.Min(min, center - Vector3.one * radius);
                    max = Vector3.Max(max, center + Vector3.one * radius);
                    largestSize = Mathf.Max(largestSize, size * Mathf.Max(1f, aspect));
                }
            }

            spriteBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                sprites.Length,
                SpriteStrideBytes);
            spriteBuffer.SetData(sprites);
            spriteCount = sprites.Length;

            if (min.x > max.x)
                throw new InvalidDataException("The sprite file contains no valid positions.");

            worldBounds = new Bounds((min + max) * 0.5f, max - min);
            worldBounds.Expand(Mathf.Max(largestSize * 2f, 10f));

            CreateQuad();
            CreateCloudTexture();

            spriteMaterial = new Material(spriteShader)
            {
                name = "Dust Spatial Sprite Material",
                enableInstancing = true,
                hideFlags = HideFlags.HideAndDontSave
            };
            spriteMaterial.renderQueue = (int)RenderQueue.Transparent;

            properties = new MaterialPropertyBlock();
            properties.SetBuffer(SpriteBufferId, spriteBuffer);
            properties.SetTexture(CloudTextureId, cloudTexture);
            properties.SetColor(TintId, spriteTint);
            properties.SetFloat(OpacityId, opacity);

            Debug.Log(
                $"DustSpriteRenderer: Loaded {spriteCount:N0} spatial sprites from {path}. " +
                $"Bounds min={worldBounds.min}, max={worldBounds.max}.", this);
        }
        catch (Exception exception)
        {
            ReleaseGpuResources();
            Debug.LogError($"DustSpriteRenderer: Failed to load sprite data.\n{exception}", this);
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static string ResolveDataPath(string relativePath)
    {
#if UNITY_EDITOR
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath));
#elif UNITY_ANDROID
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var obbDirectory = activity.Call<AndroidJavaObject>("getObbDir"))
        {
            string obbRoot = obbDirectory.Call<string>("getAbsolutePath");
            if (string.IsNullOrEmpty(obbRoot))
                throw new IOException("Android getObbDir() returned an empty path.");
            return Path.Combine(obbRoot, relativePath);
        }
#else
        return Path.Combine(Application.persistentDataPath, relativePath);
#endif
    }

    private void CreateQuad()
    {
        quadMesh = new Mesh { name = "Dust Sprite Quad" };
        quadMesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f)
        };
        quadMesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };
        quadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        quadMesh.UploadMeshData(true);
    }

    private void CreateCloudTexture()
    {
        cloudTexture = new Texture2D(
            CloudTextureSize, CloudTextureSize,
            TextureFormat.RGBA32, false, true)
        {
            name = "Generated Dust Cloud Mask",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color32[] pixels = new Color32[CloudTextureSize * CloudTextureSize];
        const float offsetX = 17.31f;
        const float offsetY = 43.77f;

        for (int y = 0; y < CloudTextureSize; y++)
        {
            for (int x = 0; x < CloudTextureSize; x++)
            {
                float fx = x;
                float fy = y;
                float broad = Mathf.PerlinNoise((fx + offsetX) * 0.025f, (fy + offsetY) * 0.025f);
                float medium = Mathf.PerlinNoise((fx + offsetY) * 0.060f, (fy + offsetX) * 0.060f);
                float fine = Mathf.PerlinNoise((fx + 9.2f) * 0.13f, (fy + 81.6f) * 0.13f);
                float noise = Mathf.Clamp01(broad * 0.56f + medium * 0.31f + fine * 0.13f);
                byte value = (byte)Mathf.RoundToInt(noise * 255f);
                pixels[y * CloudTextureSize + x] = new Color32(value, value, value, 255);
            }
        }

        cloudTexture.SetPixels32(pixels);
        cloudTexture.Apply(false, true);
    }

    private void OnDestroy()
    {
        ReleaseGpuResources();
    }

    private void ReleaseGpuResources()
    {
        if (spriteBuffer != null)
        {
            spriteBuffer.Release();
            spriteBuffer = null;
        }

        if (spriteMaterial != null)
        {
            if (Application.isPlaying) Destroy(spriteMaterial);
            else DestroyImmediate(spriteMaterial);
            spriteMaterial = null;
        }

        if (quadMesh != null)
        {
            if (Application.isPlaying) Destroy(quadMesh);
            else DestroyImmediate(quadMesh);
            quadMesh = null;
        }

        if (cloudTexture != null)
        {
            if (Application.isPlaying) Destroy(cloudTexture);
            else DestroyImmediate(cloudTexture);
            cloudTexture = null;
        }

        properties = null;
        spriteCount = 0;
    }
}
