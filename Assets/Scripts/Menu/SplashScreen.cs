using System.Collections;
using UnityEngine;

public class SplashScreen : MonoBehaviour
{
    public float splashDuration = 3.0f;

    void Start()
    {
        StartCoroutine(SplashRoutine());
    }

    IEnumerator SplashRoutine()
    {
        yield return new WaitForSeconds(splashDuration);
        Destroy(gameObject);
    }
}