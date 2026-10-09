using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

public class FastTravelMenu : MonoBehaviour
{
    [Header("Player")]
    public VRPlayerController playerController;

    [Header("Binary Source Data")]
    [Tooltip("Path relative to the project root in the Editor, or next to the built game's data folder in a desktop build.")]
    public string landmarkBinaryRelativePath = "Data/Landmarks/landmarks.bin";

    [Header("UI")]
    public GameObject landmarkButtonPrefab;
    public Transform content;
    public ScrollRect scrollRect;

    [Header("Menu")]
    public GameObject menuRoot;

    private readonly List<LandmarkEntry> landmarks = new List<LandmarkEntry>();
    private readonly List<Button> buttons = new List<Button>();

    private bool stickReleased = true;
    private int ignoreSelectionFrame = -1;

    private class LandmarkEntry
    {
        public string name;
        public string typeCode;
        public Vector3 position; // Already converted to Unity XYZ by the Python converter.
    }

    private void Start()
    {
        if (menuRoot != null)
            menuRoot.SetActive(false);

        StartCoroutine(LoadBinaryAndGenerateButtons());
    }

    private IEnumerator LoadBinaryAndGenerateButtons()
    {
        string path = ResolveLandmarkBinaryPath();
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (System.Exception ex)
        {
            Debug.LogError(
                $"FastTravelMenu: Could not read landmark binary '{path}': {ex.Message}"
            );
            yield break;
        }

        List<LandmarkBinaryRecord> records;
        try
        {
            records = LandmarkBinaryReader.Read(bytes);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"FastTravelMenu: Invalid landmark binary: {ex.Message}");
            yield break;
        }

        GenerateLandmarkButtons(records);
        Debug.Log($"FastTravelMenu: Loaded {records.Count:N0} landmarks from '{path}'.");
    }

    private string ResolveLandmarkBinaryPath()
    {
#if UNITY_EDITOR
        // Project root is the parent of Assets; use the one canonical Data/ file.
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, landmarkBinaryRelativePath));
#elif UNITY_ANDROID
        // Unity does not automatically package a project-root Data/ folder into an Android build.
        Debug.LogError(
            "FastTravelMenu: The landmark binary is stored in project-root Data/Landmarks, " +
            "which Unity does not include in Android builds automatically. " +
            "Your Android build pipeline must package this external Data folder for runtime access."
        );
        return null;
#else
        // For desktop players, expect Data/ beside the executable's <Game>_Data directory.
        DirectoryInfo gameDataParent = Directory.GetParent(Application.dataPath);
        if (gameDataParent == null)
        {
            Debug.LogError("FastTravelMenu: Could not determine the desktop build directory.");
            return null;
        }
        return Path.GetFullPath(Path.Combine(gameDataParent.FullName, landmarkBinaryRelativePath));
#endif
    }

    private void Update()
    {
        if (!IsOpen() || EventSystem.current == null)
            return;

        GameObject selected = EventSystem.current.currentSelectedGameObject;
        if (selected == null)
            return;

        Button button = selected.GetComponent<Button>();
        if (button == null || !buttons.Contains(button))
            return;

        ScrollToButton(button);
    }

    private void GenerateLandmarkButtons(IList<LandmarkBinaryRecord> records)
    {
        if (landmarkButtonPrefab == null)
        {
            Debug.LogError("FastTravelMenu: No landmark button prefab assigned.");
            return;
        }

        if (content == null)
        {
            Debug.LogError("FastTravelMenu: No Content transform assigned.");
            return;
        }

        foreach (LandmarkBinaryRecord record in records)
        {
            string typeCode = (record.Type ?? string.Empty).Trim().ToLowerInvariant();

            // The Local Bubble is not a travel destination.
            if (typeCode == "bubble" || typeCode.Contains("bubble"))
                continue;

            landmarks.Add(new LandmarkEntry
            {
                name = record.Name,
                typeCode = typeCode,
                position = record.UnityPosition
            });

            GameObject buttonObject = Instantiate(landmarkButtonPrefab, content);

            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 580f);
            }

            buttonObject.name = $"LandmarkButton_{record.Name}";

            Button button = buttonObject.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogError(
                    $"Button prefab '{landmarkButtonPrefab.name}' does not have a Button component."
                );
                Destroy(buttonObject);
                landmarks.RemoveAt(landmarks.Count - 1);
                continue;
            }

            TMP_Text text = buttonObject.GetComponentInChildren<TMP_Text>();
            if (text != null)
                text.text = record.Name;

            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.Automatic;
            button.navigation = navigation;

            int index = landmarks.Count - 1;
            button.onClick.AddListener(() => SelectLandmark(index));
            buttons.Add(button);
        }
    }

    // Desktop W/S and arrow-key navigation.
    public void MoveSelection(int direction)
    {
        if (buttons.Count == 0 || EventSystem.current == null)
            return;

        GameObject currentObject = EventSystem.current.currentSelectedGameObject;
        Button currentButton = currentObject != null
            ? currentObject.GetComponent<Button>()
            : null;

        int currentIndex = currentButton != null ? buttons.IndexOf(currentButton) : -1;
        if (currentIndex < 0)
            currentIndex = 0;

        int newIndex = currentIndex + direction;
        if (newIndex < 0)
            newIndex = buttons.Count - 1;
        if (newIndex >= buttons.Count)
            newIndex = 0;

        EventSystem.current.SetSelectedGameObject(buttons[newIndex].gameObject);
        buttons[newIndex].Select();
    }

    // VR stick navigation.
    public void HandleStickNavigation(Vector2 stick)
    {
        if (Mathf.Abs(stick.y) < 0.3f)
        {
            stickReleased = true;
            return;
        }

        if (!stickReleased)
            return;

        if (stick.y > 0.3f)
            MoveSelection(-1);
        else if (stick.y < -0.3f)
            MoveSelection(1);

        stickReleased = false;
    }

    // Keeps the currently selected button visible.
    private void ScrollToButton(Button button)
    {
        if (scrollRect == null || scrollRect.content == null || button == null)
            return;

        RectTransform viewport = scrollRect.viewport;
        if (viewport == null)
            return;

        RectTransform contentRect = scrollRect.content;
        RectTransform selectedRect = button.GetComponent<RectTransform>();
        if (selectedRect == null)
            return;

        Canvas.ForceUpdateCanvases();

        Bounds selectedBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(
            contentRect,
            selectedRect
        );

        float contentHeight = contentRect.rect.height;
        float viewportHeight = viewport.rect.height;
        float scrollableHeight = contentHeight - viewportHeight;

        if (scrollableHeight <= 0f)
        {
            scrollRect.verticalNormalizedPosition = 1f;
            return;
        }

        float contentTop = contentRect.rect.yMax;
        float selectedCenter = selectedBounds.center.y;
        float scrollAmount = contentTop - viewportHeight * 0.5f - selectedCenter;
        float normalizedPosition = 1f - scrollAmount / scrollableHeight;
        scrollRect.verticalNormalizedPosition = Mathf.Clamp01(normalizedPosition);
    }

    // Select the button currently selected by the EventSystem.
    public void SelectCurrentLandmark()
    {
        if (Time.frameCount == ignoreSelectionFrame || EventSystem.current == null)
            return;

        GameObject selectedObject = EventSystem.current.currentSelectedGameObject;
        if (selectedObject == null)
            return;

        Button selectedButton = selectedObject.GetComponent<Button>();
        if (selectedButton == null)
            return;

        int index = buttons.IndexOf(selectedButton);
        if (index < 0)
            return;

        SelectLandmark(index);
    }

    private void SelectLandmark(int index)
    {
        // Ignore a selection event occurring in the frame the menu opened.
        if (Time.frameCount == ignoreSelectionFrame)
            return;

        if (index < 0 || index >= landmarks.Count)
            return;

        LandmarkEntry landmark = landmarks[index];
        if (playerController != null)
        {
            CloseMenu();
            playerController.enabled = true;
            playerController.StartFastTravel(landmark.position);
        }
        else
        {
            Debug.LogError("FastTravelMenu: Player Controller is not assigned.");
        }
    }

    public bool IsOpen()
    {
        return menuRoot != null && menuRoot.activeSelf;
    }

    public void OpenMenu()
    {
        if (menuRoot == null)
            return;

        menuRoot.SetActive(true);
        ignoreSelectionFrame = Time.frameCount;
        Canvas.ForceUpdateCanvases();

        if (buttons.Count > 0 && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(buttons[0].gameObject);
            buttons[0].Select();
            ScrollToButton(buttons[0]);
        }
    }

    public void CloseMenu()
    {
        if (menuRoot != null)
            menuRoot.SetActive(false);
    }

    public void ToggleMenu()
    {
        if (menuRoot == null)
            return;

        if (menuRoot.activeSelf)
            CloseMenu();
        else
            OpenMenu();
    }
}
