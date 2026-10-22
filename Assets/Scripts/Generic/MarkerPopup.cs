using TMPro;
using UnityEngine;

public class MarkerPopup : MonoBehaviour
{
    [Header("Main Menu")]
    public MenuController menuController;

    [Header("Text References")]
    public TMP_Text titleText;
    public TMP_Text shortText;
    public TMP_Text paragraphText;

    private void Start()
    {
        gameObject.SetActive(false);
    }

    public void Show(MarkerInfo info)
    {
        if (info == null)
            return;

        gameObject.SetActive(true);

        if (menuController != null)
            menuController.SetPlayerControlsEnabled(false);

        if (titleText != null)
            titleText.text = info.objectName;

        if (shortText != null)
            shortText.text = info.shortDescription;

        if (paragraphText != null)
            paragraphText.text = info.paragraphDescription;

        Canvas.ForceUpdateCanvases();
    }

    public void Hide()
    {
        gameObject.SetActive(false);

        if (menuController != null)
            menuController.SetPlayerControlsEnabled(true);
    }
}