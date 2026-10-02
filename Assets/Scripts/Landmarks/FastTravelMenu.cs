using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

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

    private int selectedIndex = 0;

    private bool stickReleased = true;

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
                continue;
            }

            TMP_Text text =
                buttonObject.GetComponentInChildren<TMP_Text>();

            if (text != null)
            {
                text.text = name;
            }

            int index =
                landmarks.Count - 1;

            button.onClick.AddListener(
                () => SelectLandmark(index)
            );

            buttons.Add(button);
        }

        if (buttons.Count > 0)
        {
            selectedIndex = 0;
            UpdateSelection();
        }
    }

    public void MoveSelection(int direction)
    {
        if (buttons.Count == 0)
        {
            return;
        }

        selectedIndex += direction;

        if (selectedIndex < 0)
        {
            selectedIndex = buttons.Count - 1;
        }

        if (selectedIndex >= buttons.Count)
        {
            selectedIndex = 0;
        }

        UpdateSelection();
        ScrollToSelected();
    }

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

    private void UpdateSelection()
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            ColorBlock colors =
                buttons[i].colors;

            if (i == selectedIndex)
            {
                colors.normalColor = new Color(0.75f, 0.75f, 0.75f);
            }
            else
            {
                colors.normalColor = Color.white;
            }

            buttons[i].colors =
                colors;
        }
    }

    private void ScrollToSelected()
    {
        if (scrollRect == null ||
            scrollRect.content == null ||
            selectedIndex < 0 ||
            selectedIndex >= buttons.Count)
        {
            return;
        }

        RectTransform viewport = scrollRect.viewport;

        if (viewport == null)
        {
            return;
        }

        RectTransform contentRect =
            scrollRect.content;

        RectTransform selectedRect =
            buttons[selectedIndex].GetComponent<RectTransform>();

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

        // Position that puts the selected item near
        // the center of the visible area.
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

    public void SelectCurrentLandmark()
    {
        if (selectedIndex < 0 ||
            selectedIndex >= landmarks.Count)
        {
            return;
        }

        SelectLandmark(selectedIndex);
    }

    private void SelectLandmark(int index)
    {
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

        selectedIndex = 0;

        UpdateSelection();
        ScrollToSelected();
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