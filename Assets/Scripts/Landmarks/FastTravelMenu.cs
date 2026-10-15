using System;
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
    public Transform playerTransform;

    [Header("Fast Travel")]
    public float fastTravelDuration = 3f;

    public bool IsFastTraveling { get; private set; }

    [Header("Binary Source Data")]
    [Tooltip("Path inside the Data dir.")]
    public string landmarkBinaryDataPath = "Landmarks/landmarks.bin";

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
        string path = Path.Combine(DataPath.Root, landmarkBinaryDataPath);

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
        if (Time.frameCount == ignoreSelectionFrame)
            return;

        if (index < 0 || index >= landmarks.Count)
            return;

        if (playerTransform == null)
        {
            Debug.LogError("FastTravelMenu: Player Transform is not assigned.");
            return;
        }

        Vector3 destination = landmarks[index].position;

        StartCoroutine(FastTravelTo(destination));
        CloseMenu();
    }

    private IEnumerator FastTravelTo(Vector3 destination)
    {
        IsFastTraveling = true;

        Vector3 start = playerTransform.position;
        float duration = Mathf.Max(0.01f, fastTravelDuration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / duration);
            float smoothT = t * t * (3f - 2f * t);

            playerTransform.position =
                Vector3.Lerp(start, destination, smoothT);

            yield return null;
        }

        playerTransform.position = destination;
        IsFastTraveling = false;

        Debug.Log("Fast travel complete.");
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
