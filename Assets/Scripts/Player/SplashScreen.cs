using System.Collections;
using UnityEngine;

public class SplashScreen : MonoBehaviour
{
    public float splashDuration = 3.0f;

    public GameObject sunMarker;
    public GameObject localBubble;

    void Start()
    {
        if (sunMarker != null)
            sunMarker.SetActive(false);

        if (localBubble != null)
            localBubble.SetActive(false);

        StartCoroutine(SplashRoutine());
    }

    IEnumerator SplashRoutine()
    {
        yield return new WaitForSeconds(splashDuration);

        if (sunMarker != null)
            sunMarker.SetActive(true);

        if (localBubble != null)
            localBubble.SetActive(true);

        Destroy(gameObject);
    }
}