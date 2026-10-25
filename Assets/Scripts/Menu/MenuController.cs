using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using XRNode = UnityEngine.XR.XRNode;

public class MenuController : MonoBehaviour
{
    [Header("Main Menu References")]
    public GameObject menuCanvas;

    [Header("Information Canvas")]
    public GameObject infoCanvas;
    public TextMeshProUGUI infoTitleText;
    public TextMeshProUGUI infoBodyText;

    [Header("Settings Menu")]
    [SerializeField] private Button settingsButton;
    [SerializeField] private Canvas settingsCanvas;

    [Header("Other Menus")]
    public FastTravelMenu fastTravelMenu;
    public GaiaStarFilterMenu starFilterMenu;
    public ClusterFilterMenu clusterFilterMenu;
    public MarkerPopup markerPopup;

    [Header("Player References")]
    public VRPlayerController vrPlayerController;
    public DesktopPlayerController desktopPlayerController;

    [Header("Link")]
    public string moreUrl = "https://star-formation.com/";

    private bool menuOpen = false;
    private bool fastTravelWasOpen = false;

    private bool previousMenuButton = false;

    private void Start()
    {
        menuOpen = false;
        fastTravelWasOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        if (settingsCanvas != null)
            settingsCanvas.gameObject.SetActive(false);

        ClearCurrentSelection();
        SetPlayerControlsEnabled(true);
    }

    private void Update()
    {
        HandleKeyboardInput();
        HandleVRInput();
        MonitorFastTravel();
    }

    private void LateUpdate()
    {
        if (EventSystem.current == null)
            return;

        bool clearSelection =
            (menuCanvas != null && menuCanvas.activeInHierarchy) ||
            (infoCanvas != null && infoCanvas.activeInHierarchy) ||
            (starFilterMenu != null && starFilterMenu.IsOpen()) ||
            (clusterFilterMenu != null && clusterFilterMenu.IsOpen()) ||
            (settingsCanvas != null && settingsCanvas.gameObject.activeInHierarchy);

        if (!clearSelection)
            return;

        GameObject selected =
            EventSystem.current.currentSelectedGameObject;

        if (selected == null)
            return;

        // Keep input fields selected so they retain focus.
        if (selected.GetComponentInParent<TMP_InputField>() != null)
            return;

        // Clear selection from other UI elements.
        ClearCurrentSelection();
    }

    private void ClearCurrentSelection()
    {
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    private void MonitorFastTravel()
    {
        bool fastTravelActive =
            fastTravelMenu != null &&
            fastTravelMenu.IsOpen();

        if (fastTravelWasOpen &&
            !fastTravelActive &&
            !menuOpen)
        {
            ClearCurrentSelection();
            SetPlayerControlsEnabled(true);
        }

        fastTravelWasOpen = fastTravelActive;
    }

    private void HandleKeyboardInput()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
            return;

        if (keyboard.escapeKey.wasPressedThisFrame ||
            keyboard.mKey.wasPressedThisFrame)
        {
            HandleMenuKeyboardToggle();
        }
    }

    private void HandleMenuKeyboardToggle()
    {
        // Information / Controls
        if (infoCanvas != null && infoCanvas.activeSelf)
        {
            CloseInfoMenu();
            SetPlayerControlsEnabled(true);
            return;
        }

        // Star Filter
        if (starFilterMenu != null &&
            starFilterMenu.IsOpen())
        {
            starFilterMenu.CloseMenu();
            ClearCurrentSelection();
            SetPlayerControlsEnabled(true);
            return;
        }

        // Cluster Filter
        if (clusterFilterMenu != null &&
            clusterFilterMenu.IsOpen())
        {
            clusterFilterMenu.CloseMenu();
            ClearCurrentSelection();
            SetPlayerControlsEnabled(true);
            return;
        }

        // Fast Travel
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            fastTravelMenu.CloseMenu();
            ClearCurrentSelection();
            SetPlayerControlsEnabled(true);
            return;
        }

        // Landmark Popup
        if (markerPopup != null &&
            markerPopup.gameObject.activeSelf)
        {
            markerPopup.Hide();
            ClearCurrentSelection();
            SetPlayerControlsEnabled(true);
            return;
        }

        // Settings Menu
        if (settingsCanvas != null && settingsCanvas.gameObject.activeSelf)
        {
            CloseSettings();
            return;
        }

        // Main Menu
        if (menuOpen)
            CloseMenu();
        else
            OpenMenu();
    }

    private void HandleVRInput()
    {
        XRInputDevice leftHand =
            XRInputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        bool menuButton = false;
        bool leftHandValid = leftHand.isValid;

        if (leftHandValid)
        {
            // Left controller Menu button.
            leftHand.TryGetFeatureValue(
                XRCommonUsages.menuButton,
                out menuButton);
        }

        bool menuPressed =
            menuButton && !previousMenuButton;

        previousMenuButton = menuButton;

        // Keep the Menu button functionality.
        if (menuPressed)
        {
            HandleMenuKeyboardToggle();
            return;
        }

        // Information screens do not use stick navigation.
        if (infoCanvas != null && infoCanvas.activeSelf)
            return;

        // Preserve Fast Travel's existing left-stick navigation.
        if (fastTravelMenu != null && fastTravelMenu.IsOpen())
        {
            if (leftHandValid)
            {
                Vector2 leftStick = Vector2.zero;

                leftHand.TryGetFeatureValue(
                    XRCommonUsages.primary2DAxis,
                    out leftStick);

                fastTravelMenu.HandleStickNavigation(leftStick);
            }

            return;
        }

        // No manual X-button selection.
        // Controller pointing, hovering, and clicking are handled by the UI.
    }

    private void OpenMenu()
    {
        menuOpen = true;
        fastTravelWasOpen = false;

        ClearCurrentSelection();

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        if (menuCanvas != null)
            menuCanvas.SetActive(true);

        SetPlayerControlsEnabled(false);

        Canvas.ForceUpdateCanvases();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        ClearCurrentSelection();
        SetPlayerControlsEnabled(true);
    }

    public void SetPlayerControlsEnabled(bool enabled)
    {
        if (vrPlayerController != null)
            vrPlayerController.enabled = enabled;

        if (desktopPlayerController != null)
            desktopPlayerController.enabled = enabled;

        // Manage the mouse cursor in desktop mode only.
        if (desktopPlayerController != null &&
            !UnityEngine.XR.XRSettings.isDeviceActive)
        {
            if (enabled)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }

    public void ShowControls()
    {
        string controls = "";

        if (desktopPlayerController != null)
        {
            controls += "Desktop:\n" + desktopPlayerController.GetControls();
        }

        if (vrPlayerController != null)
        {
            if (!string.IsNullOrEmpty(controls))
                controls += "\n\n";

            controls += "VR:\n" + vrPlayerController.GetControls();
        }

        OpenInfoMenu("Controls", controls);
    }

    public void ShowCredits()
    {
        string credits = string.Join("\n", new[]
        {

        // Project team and acknowledgments.
        "PROJECT TEAM AND ACKNOWLEDGMENTS",
        "Star Formation and Interstellar Medium Laboratory",
        "Technion – Israel Institute of Technology",
        "",
        "Scientific Guidance and Project Supervision",
        "Prof. Shmuel Bialy",
        "",
        "Initial VR Dust Simulation",
        "Benny Bairrington",
        "",
        "Stellar Simulation and Development",
        "Zakhar Pecherichny",
        "",
        "This project builds upon the work initiated by Benny Bairrington, who began developing the VR space simulation to visualize interstellar dust. The project was subsequently continued by Zakhar Pecherichny, who extended the simulation to include stars. The project was carried out under the scientific guidance and supervision of Prof. Shmuel Bialy.",
        "",

        // Background image.
        "DATA AND ASSET CREDITS",
        "",
        "MILKY WAY BACKGROUND",
        "The Milky Way Panorama",
        "Credit: ESO/S. Brunier",
        "Source: https://www.eso.org/public/images/eso0932a/",
        "License: Creative Commons Attribution 4.0 International (CC BY 4.0)",
        "License URL: https://creativecommons.org/licenses/by/4.0/",
        "",

        // Gaia DR3.
        "GAIA DR3 STAR CATALOGUE",
        "Credit: European Space Agency (ESA), Gaia mission, and Gaia Data Processing and Analysis Consortium (DPAC).",
        "Source: https://www.cosmos.esa.int/gaia",
        "References: Gaia Collaboration et al. (2016), Astronomy & Astrophysics, 595, A1; Gaia Collaboration et al. (2023), Astronomy & Astrophysics, 674, A1.",
        "License: CC BY-NC 3.0 IGO",
        "License URL: https://creativecommons.org/licenses/by-nc/3.0/igo/",
        "Commercial use requires separate authorization. See GAIA_README.md.",
        "",

        // Open cluster catalogue.
        "OPEN CLUSTER CATALOGUE",
        "Hunt, E. L. & Reffert, S. (2024). Improving the open cluster census. III. Using cluster masses, radii, and dynamics to create a cleaned open cluster catalogue.",
        "Publication: Astronomy & Astrophysics, 686, A42.",
        "Article DOI: https://doi.org/10.1051/0004-6361/202348662",
        "Catalogue: CDS/VizieR J/A+A/686/A42",
        "Catalogue DOI: https://doi.org/10.26093/cds/vizier.36860042",
        "VizieR service DOI: https://doi.org/10.26093/cds/vizier",
        "See CLUSTERS_README.md for catalogue usage and licensing information.",
        "",

        // Molecular cloud landmarks.
        "MOLECULAR CLOUD LANDMARKS",
        "Selected data from Table 1, 'Skeletonization Results,' of Zucker et al. (2021) are used for in-game landmarks.",
        "Article: On the Three-Dimensional Structure of Local Molecular Clouds.",
        "Authors: Catherine Zucker et al. (2021).",
        "Publication: The Astrophysical Journal, 919, 35 (2021).",
        "Article DOI: https://doi.org/10.3847/1538-4357/ac1f96",
        "Source: https://arxiv.org/abs/2109.09765",
        "License: Creative Commons Attribution 4.0 International (CC BY 4.0), arXiv version.",
        "License URL: https://creativecommons.org/licenses/by/4.0/",
        "Changes: Selected coordinate data have been adapted for this application's visualization and coordinate system.",
        "",

        // 3D dust map.
        "THREE-DIMENSIONAL DUST MAP",
        "Edenhofer, G. et al. (2024). A parsec-scale Galactic 3D dust map out to 1.25 kpc from the Sun.",
        "Publication: Astronomy & Astrophysics, 685, A82.",
        "Article DOI: https://doi.org/10.1051/0004-6361/202347628",
        "Dataset DOI: https://doi.org/10.5281/zenodo.8187942",
        "Dataset license: Creative Commons Attribution 4.0 International (CC BY 4.0)",
        "License URL: https://creativecommons.org/licenses/by/4.0/"
    });

        OpenInfoMenu("Credits", credits);
    }

    private void OpenInfoMenu(string title, string body)
    {
        if (infoCanvas == null)
        {
            Debug.LogError(
                "MenuController: Information Canvas is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoTitleText != null)
            infoTitleText.text = title;

        if (infoBodyText != null)
            infoBodyText.text = body;

        infoCanvas.SetActive(true);
        Canvas.ForceUpdateCanvases();
    }

    private void CloseInfoMenu()
    {
        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        ClearCurrentSelection();
    }

    public void OpenFastTravel()
    {
        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "MenuController: Fast Travel Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        fastTravelWasOpen = true;
        fastTravelMenu.OpenMenu();
    }

    public void OpenStarFilter()
    {
        if (starFilterMenu == null)
        {
            Debug.LogError(
                "MenuController: Star Filter Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        starFilterMenu.OpenMenu();
    }

    public void OpenClusterFilter()
    {
        if (clusterFilterMenu == null)
        {
            Debug.LogError(
                "MenuController: Cluster Filter Menu is not assigned."
            );
            return;
        }

        menuOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        clusterFilterMenu.OpenMenu();
    }

    public void OpenSettings()
    {
        if (settingsCanvas == null)
        {
            Debug.LogError(
                "MenuController: Settings Canvas is not assigned."
            );
            return;
        }

        menuOpen = false;
        fastTravelWasOpen = false;

        ClearCurrentSelection();

        if (menuCanvas != null)
            menuCanvas.SetActive(false);

        if (infoCanvas != null)
            infoCanvas.SetActive(false);

        settingsCanvas.gameObject.SetActive(true);

        SetPlayerControlsEnabled(false);

        Canvas.ForceUpdateCanvases();
    }

    public void CloseSettings()
    {
        if (settingsCanvas != null)
            settingsCanvas.gameObject.SetActive(false);

        ClearCurrentSelection();
        SetPlayerControlsEnabled(true);
    }

    public void OpenLink()
    {
        Application.OpenURL(moreUrl);
    }

    public void Quit()
    {
        Application.Quit();
    }
}