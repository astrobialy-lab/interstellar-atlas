using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

public class FastTravelMenu : MonoBehaviour
{
    [Header("Player")]
    public VRPlayerController playerController;

    [Header("Source Data")]
    public TextAsset landmarkCsv;

    [Header("UI")]
    public GameObject landmarkButtonPrefab;
    public Transform content;
    public ScrollRect scrollRect;

    [Header("Menu")]
    public GameObject menuRoot;

    private readonly List<LandmarkEntry> landmarks =
        new List<LandmarkEntry>();

    private readonly List<Button> buttons =
        new List<Button>();

    private bool stickReleased = true;

    // Prevents the same input that opens the menu
    // from immediately activating the first destination.
    private int ignoreSelectionFrame = -1;

    private class LandmarkEntry
    {
        public string name;
        public string typeCode;
        public Vector3 position;
    }

    private void Start()
    {
        GenerateLandmarkButtons();

        if (menuRoot != null)
        {
            menuRoot.SetActive(false);
        }
    }

    private void Update()
    {
        if (!IsOpen() ||
            EventSystem.current == null)
        {
            return;
        }

        GameObject selected =
            EventSystem.current.currentSelectedGameObject;

        if (selected == null)
        {
            return;
        }

        Button button =
            selected.GetComponent<Button>();

        if (button == null ||
            !buttons.Contains(button))
        {
            return;
        }

        ScrollToButton(button);
    }

    private void GenerateLandmarkButtons()
    {
        if (landmarkCsv == null)
        {
            Debug.LogError(
                "FastTravelMenu: No landmark CSV assigned."
            );

            return;
        }

        if (landmarkButtonPrefab == null)
        {
            Debug.LogError(
                "FastTravelMenu: No landmark button prefab assigned."
            );

            return;
        }

        if (content == null)
        {
            Debug.LogError(
                "FastTravelMenu: No Content transform assigned."
            );

            return;
        }

        string[] lines =
            landmarkCsv.text.Split('\n');

        for (int i = 1; i < lines.Length; i++)
        {
            string line =
                lines[i].Trim();

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            string[] parts =
                line.Split(',');

            if (parts.Length < 8)
            {
                Debug.LogWarning(
                    $"Skipping malformed landmark row {i + 1}: {line}"
                );

                continue;
            }

            string name =
                parts[0].Trim();

            string typeCode =
                parts[2].Trim().ToLowerInvariant();

            // The Local Bubble is not a travel destination.
            if (typeCode == "bubble")
            {
                continue;
            }

            bool validX =
                float.TryParse(
                    parts[3],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float x
                );

            bool validY =
                float.TryParse(
                    parts[4],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float y
                );

            bool validZ =
                float.TryParse(
                    parts[5],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float z
                );

            if (!validX || !validY || !validZ)
            {
                Debug.LogWarning(
                    $"Invalid coordinates for landmark '{name}'."
                );

                continue;
            }

            LandmarkEntry landmark =
                new LandmarkEntry
                {
                    name = name,
                    typeCode = typeCode,
                    position = new Vector3(x, y, z)
                };

            landmarks.Add(landmark);

            GameObject buttonObject =
                Instantiate(
                    landmarkButtonPrefab,
                    content
                );

            RectTransform rect = buttonObject.GetComponent<RectTransform>();

            if (rect != null)
            {
                rect.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Horizontal,
                    580f
                );
            }

            buttonObject.name =
                $"LandmarkButton_{name}";

            Button button =
                buttonObject.GetComponent<Button>();

            if (button == null)
            {
                Debug.LogError(
                    $"Button prefab '{landmarkButtonPrefab.name}' " +
                    "does not have a Button component."
                );

                Destroy(buttonObject);

                landmarks.RemoveAt(
                    landmarks.Count - 1
                );

                continue;
            }

            TMP_Text text =
                buttonObject.GetComponentInChildren<TMP_Text>();

            if (text != null)
            {
                text.text = name;
            }

            /*
             * Let Unity handle navigation between buttons.
             */
            Navigation navigation =
                button.navigation;

            navigation.mode =
                Navigation.Mode.Automatic;

            button.navigation =
                navigation;

            int index =
                landmarks.Count - 1;

            button.onClick.AddListener(
                () => SelectLandmark(index)
            );

            buttons.Add(button);
        }
    }

    /*
     * Desktop W/S and arrow-key navigation.
     *
     * EventSystem remains the single source of truth.
     */
    public void MoveSelection(int direction)
    {
        if (buttons.Count == 0 ||
            EventSystem.current == null)
        {
            return;
        }

        GameObject currentObject =
            EventSystem.current.currentSelectedGameObject;

        Button currentButton =
            currentObject != null
                ? currentObject.GetComponent<Button>()
                : null;

        int currentIndex =
            currentButton != null
                ? buttons.IndexOf(currentButton)
                : -1;

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int newIndex =
            currentIndex + direction;

        if (newIndex < 0)
        {
            newIndex = buttons.Count - 1;
        }

        if (newIndex >= buttons.Count)
        {
            newIndex = 0;
        }

        EventSystem.current.SetSelectedGameObject(
            buttons[newIndex].gameObject
        );

        buttons[newIndex].Select();
    }

    /*
     * VR stick navigation.
     */
    public void HandleStickNavigation(Vector2 stick)
    {
        if (Mathf.Abs(stick.y) < 0.3f)
        {
            stickReleased = true;
            return;
        }

        if (!stickReleased)
        {
            return;
        }

        if (stick.y > 0.3f)
        {
            MoveSelection(-1);
        }
        else if (stick.y < -0.3f)
        {
            MoveSelection(1);
        }

        stickReleased = false;
    }

    /*
     * Keeps the currently selected Button visible.
     */
    private void ScrollToButton(Button button)
    {
        if (scrollRect == null ||
            scrollRect.content == null ||
            button == null)
        {
            return;
        }

        RectTransform viewport =
            scrollRect.viewport;

        if (viewport == null)
        {
            return;
        }

        RectTransform contentRect =
            scrollRect.content;

        RectTransform selectedRect =
            button.GetComponent<RectTransform>();

        if (selectedRect == null)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();

        Bounds selectedBounds =
            RectTransformUtility.CalculateRelativeRectTransformBounds(
                contentRect,
                selectedRect
            );

        float contentHeight =
            contentRect.rect.height;

        float viewportHeight =
            viewport.rect.height;

        float scrollableHeight =
            contentHeight - viewportHeight;

        if (scrollableHeight <= 0f)
        {
            scrollRect.verticalNormalizedPosition = 1f;
            return;
        }

        float contentTop =
            contentRect.rect.yMax;

        float selectedCenter =
            selectedBounds.center.y;

        float scrollAmount =
            contentTop -
            viewportHeight * 0.5f -
            selectedCenter;

        float normalizedPosition =
            1f -
            scrollAmount / scrollableHeight;

        scrollRect.verticalNormalizedPosition =
            Mathf.Clamp01(normalizedPosition);
    }

    /*
     * Select the Button currently selected by the EventSystem.
     */
    public void SelectCurrentLandmark()
    {
        if (Time.frameCount == ignoreSelectionFrame)
        {
            return;
        }

        if (EventSystem.current == null)
        {
            return;
        }

        GameObject selectedObject =
            EventSystem.current.currentSelectedGameObject;

        if (selectedObject == null)
        {
            return;
        }

        Button selectedButton =
            selectedObject.GetComponent<Button>();

        if (selectedButton == null)
        {
            return;
        }

        int index =
            buttons.IndexOf(selectedButton);

        if (index < 0)
        {
            return;
        }

        SelectLandmark(index);
    }

    private void SelectLandmark(int index)
    {
        /*
         * Prevent a mouse click or VR/keyboard submit
         * occurring in the same frame that opened the menu
         * from activating the first destination.
         */
        if (Time.frameCount == ignoreSelectionFrame)
        {
            return;
        }

        if (index < 0 ||
            index >= landmarks.Count)
        {
            return;
        }

        LandmarkEntry landmark =
            landmarks[index];

        if (playerController != null)
        {
            CloseMenu();

            playerController.enabled = true;

            playerController.StartFastTravel(
                landmark.position
            );
        }
        else
        {
            Debug.LogError(
                "FastTravelMenu: Player Controller is not assigned."
            );
        }
    }

    public bool IsOpen()
    {
        return menuRoot != null &&
               menuRoot.activeSelf;
    }

    public void OpenMenu()
    {
        if (menuRoot == null)
        {
            return;
        }

        menuRoot.SetActive(true);

        /*
         * Ignore any selection event occurring in the
         * same frame that the menu was opened.
         */
        ignoreSelectionFrame = Time.frameCount;

        Canvas.ForceUpdateCanvases();

        /*
         * First destination is selected/highlighted.
         */
        if (buttons.Count > 0 &&
            EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);

            EventSystem.current.SetSelectedGameObject(
                buttons[0].gameObject
            );

            buttons[0].Select();

            ScrollToButton(buttons[0]);
        }
    }

    public void CloseMenu()
    {
        if (menuRoot == null)
        {
            return;
        }

        menuRoot.SetActive(false);
    }

    public void ToggleMenu()
    {
        if (menuRoot == null)
        {
            return;
        }

        if (menuRoot.activeSelf)
        {
            CloseMenu();
        }
        else
        {
            OpenMenu();
        }
    }
}