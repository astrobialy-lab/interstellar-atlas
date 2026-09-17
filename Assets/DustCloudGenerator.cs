using UnityEngine;
using System.Globalization;

[RequireComponent(typeof(ParticleSystem))]
public class DustCloudGenerator : MonoBehaviour
{
    public TextAsset csvFile;

    public float baseParticleSize = 0.75f;
    public int defaultParticleCount = 20000;

    public float minSizeMultiplier = 0.4f;
    public float maxSizeMultiplier = 2.0f;

    [Tooltip("Amount added before wrapping from thickest back to thinnest.")]
    public float sizeMultiplierStep = 0.4f;

    [Header("Default Appearance")]
    public float initialSizeMultiplier = 2.0f;

    [Header("Color Scheme")]
    public int colorScheme = 0;
    public float alphaMin = 0.03f;
    public float alphaMax = 0.65f;

    private ParticleSystem ps;
    private ParticleSystem.Particle[] particles;
    private float[] baseSizes;
    private float[] densityVisuals;

    private float[] csvR;
    private float[] csvG;
    private float[] csvB;

    private int loadedParticleCount = 0;
    private int activeParticleCount = 0;
    private float sizeMultiplier;

    void Start()
    {
        sizeMultiplier = initialSizeMultiplier;

        GenerateCloudFromCSV();
        SetParticleCount(defaultParticleCount);
    }

    public void SetParticleCount(int count)
    {
        if (particles == null) return;

        activeParticleCount = Mathf.Clamp(count, 0, loadedParticleCount);
        ps.SetParticles(particles, activeParticleCount);

        Debug.Log("Active particles: " + activeParticleCount);
    }

    public void AdjustParticleSizeMultiplier(float delta)
    {
        if (particles == null) return;

        sizeMultiplier = Mathf.Clamp(
            sizeMultiplier + delta,
            minSizeMultiplier,
            maxSizeMultiplier
        );

        for (int i = 0; i < loadedParticleCount; i++)
        {
            particles[i].startSize = baseSizes[i] * sizeMultiplier;
        }

        ps.SetParticles(particles, activeParticleCount);
    }

    public void CycleParticleSizeMultiplier()
    {
        if (particles == null) return;

        sizeMultiplier += sizeMultiplierStep;

        if (sizeMultiplier > maxSizeMultiplier + 0.001f)
        {
            sizeMultiplier = minSizeMultiplier;
        }

        for (int i = 0; i < loadedParticleCount; i++)
        {
            particles[i].startSize = baseSizes[i] * sizeMultiplier;
        }

        ps.SetParticles(particles, activeParticleCount);

        Debug.Log("Dust thickness multiplier: " + sizeMultiplier);
    }

    public void NextColorScheme()
    {
        colorScheme++;
        if (colorScheme > 5)
        {
            colorScheme = 0;
        }

        ApplyColorScheme();
        ps.SetParticles(particles, activeParticleCount);

        Debug.Log("Color scheme changed to: " + colorScheme);
    }

    public void PreviousColorScheme()
    {
        colorScheme--;

        if (colorScheme < 0)
            {
            colorScheme = 5;
            }

        ApplyColorScheme();
        ps.SetParticles(particles, activeParticleCount);

        Debug.Log("Color scheme changed to: " + colorScheme);
    }

    void GenerateCloudFromCSV()
    {
        if (csvFile == null)
        {
            Debug.LogError("No CSV file assigned to DustCloudGenerator.");
            return;
        }

        ps = GetComponent<ParticleSystem>();

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.sortMode = ParticleSystemSortMode.None;

        if (renderer.material != null)
        {
            renderer.material.enableInstancing = true;
        }

        string[] lines = csvFile.text.Split('\n');
        int particleCapacity = lines.Length - 1;

        var main = ps.main;
        main.maxParticles = particleCapacity;
        main.startLifetime = Mathf.Infinity;
        main.startSpeed = 0f;
        main.startSize = baseParticleSize;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.loop = false;
        main.playOnAwake = false;

        var emission = ps.emission;
        emission.enabled = false;

        particles = new ParticleSystem.Particle[particleCapacity];
        baseSizes = new float[particleCapacity];
        densityVisuals = new float[particleCapacity];

        csvR = new float[particleCapacity];
        csvG = new float[particleCapacity];
        csvB = new float[particleCapacity];

        int index = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] parts = line.Split(',');
            if (parts.Length < 10) continue;

            float x = float.Parse(parts[0], CultureInfo.InvariantCulture);
            float y = float.Parse(parts[1], CultureInfo.InvariantCulture);
            float z = float.Parse(parts[2], CultureInfo.InvariantCulture);

            // Final approved dust orientation:
            // 1. Reflect across z = -x in Unity's XZ plane.
            // 2. Flip Unity Y, which represents physical/Python Z.
            // Combined transformation: (x, y, z) -> (-z, -y, -x).
            float originalX = x;
            float originalZ = z;

            x = -originalZ;
            y = -y;
            z = -originalX;

            // New CSV format:
            // x,y,z,density,density_norm,density_visual,r,g,b,alpha
            float densityVisual = float.Parse(parts[5], CultureInfo.InvariantCulture);

            float r = float.Parse(parts[6], CultureInfo.InvariantCulture);
            float g = float.Parse(parts[7], CultureInfo.InvariantCulture);
            float b = float.Parse(parts[8], CultureInfo.InvariantCulture);

            particles[index].position = new Vector3(x, y, z);

            densityVisuals[index] = Mathf.Clamp01(densityVisual);

            csvR[index] = r;
            csvG[index] = g;
            csvB[index] = b;

            baseSizes[index] = baseParticleSize;
            particles[index].startSize = baseSizes[index] * sizeMultiplier;

            particles[index].remainingLifetime = Mathf.Infinity;
            particles[index].startLifetime = Mathf.Infinity;

            index++;
        }

        loadedParticleCount = index;

        ApplyColorScheme();

        Debug.Log("Loaded particles: " + loadedParticleCount);
        Debug.Log(
            "Applied final dust orientation: (x, y, z) -> (-z, -y, -x)."
        );
    }

    void ApplyColorScheme()
    {
        for (int i = 0; i < loadedParticleCount; i++)
        {
            float t = densityVisuals[i];
            float alpha = Mathf.Lerp(alphaMin, alphaMax, t);

            Color c = ColorFromScheme(i, t);
            c.a = alpha;

            particles[i].startColor = c;
        }
    }

    Color ColorFromScheme(int currentParticle, float t)
    {
        t = Mathf.Clamp01(t);

        switch (colorScheme)
        {
            case 0:
                return new Color(csvR[currentParticle],
                    csvG[currentParticle],
                    csvB[currentParticle]);

            case 1:
                // Magma-ish: yellow/orange -> purple
                return ThreeColorGradient(
                    t,
                    new Color(0.98f, 0.85f, 0.35f),
                    new Color(0.85f, 0.35f, 0.35f),
                    new Color(0.20f, 0.05f, 0.35f)
                );

            case 2:
                // Blue/cyan/yellow
                return ThreeColorGradient(
                    t,
                    new Color(0.05f, 0.20f, 0.90f),
                    new Color(0.10f, 0.90f, 1.00f),
                    new Color(1.00f, 0.85f, 0.25f)
                );

            case 3:
                // Green/yellow/orange
                return ThreeColorGradient(
                    t,
                    new Color(0.10f, 0.45f, 0.25f),
                    new Color(0.75f, 0.85f, 0.35f),
                    new Color(1.00f, 0.45f, 0.10f)
                );

            case 4:
                // Grayscale
                return new Color(t, t, t);

            case 5:
                // Blue/white/red
                return ThreeColorGradient(
                    t,
                    new Color(0.10f, 0.25f, 1.00f),
                    new Color(0.95f, 0.95f, 0.95f),
                    new Color(1.00f, 0.20f, 0.10f)
                );

            default:
                return Color.white;
        }
    }

    Color ThreeColorGradient(float t, Color low, Color mid, Color high)
    {
        if (t < 0.5f)
        {
            return Color.Lerp(low, mid, t / 0.5f);
        }
        else
        {
            return Color.Lerp(mid, high, (t - 0.5f) / 0.5f);
        }
    }
}