using System.Collections;
using TMPro;
using UnityEngine;

public class LandmarkPopup : MonoBehaviour
{
    [Header("Text References")]
    public TMP_Text titleText;
    public TMP_Text shortText;
    public TMP_Text paragraphText;

    [Header("Viewer Placement")]
    public Transform headsetCamera;
    public float distanceFromViewer = 2.0f;
    public float verticalOffset = -0.15f;
    public bool reverseFacingDirection = true;

    [Header("Scene References")]
    public DustVisibilityController dustVisibility;

    private Coroutine hideDustCoroutine;

    private void Start()
    {
        gameObject.SetActive(false);
    }

    public void Show(LandmarkInfo info)
    {
        if (info == null)
        {
            return;
        }

        // Make the popup active before updating and positioning it.
        gameObject.SetActive(true);

        if (titleText != null)
        {
            titleText.text = info.objectName;
        }

        if (shortText != null)
        {
            shortText.text = info.shortDescription;
        }

        if (paragraphText != null)
        {
            paragraphText.text = info.paragraphDescription;
        }

        PositionInFrontOfViewer();

        Canvas.ForceUpdateCanvases();

        // Wait until the popup has rendered before hiding the dust.
        if (hideDustCoroutine != null)
        {
            StopCoroutine(hideDustCoroutine);
        }

        hideDustCoroutine = StartCoroutine(
            HideDustAtEndOfFrame()
        );
    }

    public void Hide()
    {
        if (hideDustCoroutine != null)
        {
            StopCoroutine(hideDustCoroutine);
            hideDustCoroutine = null;
        }

        if (dustVisibility != null)
        {
            dustVisibility.ShowDust();
        }

        gameObject.SetActive(false);
    }

    private IEnumerator HideDustAtEndOfFrame()
    {
        yield return new WaitForEndOfFrame();

        if (dustVisibility != null)
        {
            dustVisibility.HideDust();
        }

        hideDustCoroutine = null;
    }

    private void PositionInFrontOfViewer()
    {
        if (headsetCamera == null)
        {
            Debug.LogWarning(
                "LandmarkPopup: Headset Camera has not been assigned."
            );

            return;
        }

        Vector3 targetPosition =
            headsetCamera.position +
            headsetCamera.forward * distanceFromViewer +
            Vector3.up * verticalOffset;

        transform.position = targetPosition;

        Vector3 facingDirection;

        if (reverseFacingDirection)
        {
            facingDirection =
                transform.position - headsetCamera.position;
        }
        else
        {
            facingDirection =
                headsetCamera.position - transform.position;
        }

        transform.rotation = Quaternion.LookRotation(
            facingDirection,
            Vector3.up
        );
    }
}