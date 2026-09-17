using UnityEngine;

public class DustVisibilityController : MonoBehaviour
{
    private Renderer[] dustRenderers;

    private void Awake()
    {
        dustRenderers = GetComponentsInChildren<Renderer>(true);
    }

    public void HideDust()
    {
        SetDustVisible(false);
    }

    public void ShowDust()
    {
        SetDustVisible(true);
    }

    private void SetDustVisible(bool visible)
    {
        foreach (Renderer dustRenderer in dustRenderers)
        {
            if (dustRenderer != null)
            {
                dustRenderer.enabled = visible;
            }
        }
    }
}