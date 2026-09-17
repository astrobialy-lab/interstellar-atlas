using UnityEngine;

public class PopupTest : MonoBehaviour
{
    public LandmarkPopup popup;
    public LandmarkInfo testInfo;

    void Start()
    {
        popup.Show(testInfo);
    }
}