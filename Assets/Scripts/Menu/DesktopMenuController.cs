using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class DesktopMenuController : MonoBehaviour
{
    [Header("Menu References")]
    public GameObject menuCanvas;
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI optionText;
    public TextMeshProUGUI bodyText;
    public RectTransform bodyRect;
    public FastTravelMenu fastTravelMenu;

    [Header("Player References")]
    public VRPlayerController vrPlayerController;
    public DesktopPlayerController desktopPlayerController;

    [Header("Link")]
    public string moreUrl =
        "https://star-formation.com/";

    private bool menuOpen = false;

    private int selectedIndex = 0;

    private readonly string[] options =
    {
        "Controls",
        "About",
        "Fast Travel",
        "Show Me More",
        "Quit"
    };

    private void Start()
    {
        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = true;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = true;
        }
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
        {
            return;
        }

        HandleKeyboardInput(keyboard);
    }

    private void HandleKeyboardInput(
        Keyboard keyboard
    )
    {
        /*
         * ESCAPE ALWAYS OPENS / CLOSES THE MAIN MENU
         */
        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            if (fastTravelMenu != null &&
                fastTravelMenu.IsOpen())
            {
                fastTravelMenu.CloseMenu();

                if (menuCanvas != null)
                {
                    menuCanvas.SetActive(false);
                }

                menuOpen = false;

                RestoreSceneAfterMenu();
            }
            else if (menuOpen)
            {
                CloseMenu();
            }
            else
            {
                OpenMenu();
            }

            return;
        }

        /*
         * FAST TRAVEL MENU
         */
        if (fastTravelMenu != null &&
            fastTravelMenu.IsOpen())
        {
            HandleFastTravelKeyboard(keyboard);
            return;
        }

        /*
         * MAIN MENU CLOSED
         */
        if (!menuOpen)
        {
            return;
        }

        /*
         * MAIN MENU OPEN
         */

        if (keyboard.upArrowKey.wasPressedThisFrame ||
            keyboard.wKey.wasPressedThisFrame)
        {
            selectedIndex--;

            if (selectedIndex < 0)
            {
                selectedIndex =
                    options.Length - 1;
            }

            ShowMainMenu();
        }

        if (keyboard.downArrowKey.wasPressedThisFrame ||
            keyboard.sKey.wasPressedThisFrame)
        {
            selectedIndex++;

            if (selectedIndex >= options.Length)
            {
                selectedIndex = 0;
            }

            ShowMainMenu();
        }

        if (keyboard.enterKey.wasPressedThisFrame ||
            keyboard.spaceKey.wasPressedThisFrame)
        {
            SelectOption();
        }
    }

    private void HandleFastTravelKeyboard(
        Keyboard keyboard
    )
    {
        if (keyboard.upArrowKey.wasPressedThisFrame ||
            keyboard.wKey.wasPressedThisFrame)
        {
            fastTravelMenu.MoveSelection(-1);
        }

        if (keyboard.downArrowKey.wasPressedThisFrame ||
            keyboard.sKey.wasPressedThisFrame)
        {
            fastTravelMenu.MoveSelection(1);
        }

        if (keyboard.enterKey.wasPressedThisFrame ||
            keyboard.spaceKey.wasPressedThisFrame)
        {
            fastTravelMenu.SelectCurrentLandmark();

            menuOpen = false;

            if (menuCanvas != null)
            {
                menuCanvas.SetActive(false);
            }

            RestoreSceneAfterMenu();
        }
    }

    private void OpenMenu()
    {
        menuOpen = true;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(true);
        }

        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = false;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = false;
        }

        selectedIndex = 0;

        ShowMainMenu();

        Canvas.ForceUpdateCanvases();
    }

    private void CloseMenu()
    {
        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        RestoreSceneAfterMenu();
    }

    private void CloseFastTravel()
    {
        fastTravelMenu.CloseMenu();

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(true);
        }

        menuOpen = true;
        selectedIndex = 2;

        ShowMainMenu();
    }

    private void RestoreSceneAfterMenu()
    {
        if (vrPlayerController != null)
        {
            vrPlayerController.enabled = true;
        }

        if (desktopPlayerController != null)
        {
            desktopPlayerController.enabled = true;
        }
    }

    private void ShowMainMenu()
    {
        if (titleText != null)
        {
            titleText.text =
                "Dust Cloud Viewer";
        }

        if (optionText != null)
        {
            optionText.text = "";

            for (
                int i = 0;
                i < options.Length;
                i++
            )
            {
                if (i == selectedIndex)
                {
                    optionText.text +=
                        "> " +
                        options[i] +
                        "\n";
                }
                else
                {
                    optionText.text +=
                        "  " +
                        options[i] +
                        "\n";
                }
            }
        }

        if (bodyText != null)
        {
            bodyText.text =
                "Escape: open / close menu\n" +
                "Enter / Space: select\n" +
                "W / S or Arrow Keys: navigate";
        }
    }

    private void SelectOption()
    {
        switch (selectedIndex)
        {
            case 0:
                ShowControls();
                break;

            case 1:
                ShowAbout();
                break;

            case 2:
                OpenFastTravel();
                break;

            case 3:
                Application.OpenURL(moreUrl);
                break;

            case 4:
                Application.Quit();
                break;
        }
    }

    private void ShowControls()
    {
        if (titleText != null)
        {
            titleText.text = "Controls";
        }

        if (optionText != null)
        {
            optionText.text = "";
        }

        if (bodyText != null)
        {
            bodyText.text =
                @"W / S: forward / backward
A / D: strafe left / right
Space / Shift: move up / down
Mouse: look around
Q / E: roll
= / -: increase / decrease speed
1: next color scheme
2: cycle particle thickness
3: quality / performance particles
4 / 5: decrease / increase particle size
6: show / hide landmarks
7: select landmark
8: close landmark information
Escape: open / close menu";
        }
    }

    private void ShowAbout()
    {
        if (titleText != null)
        {
            titleText.text = "About";
        }

        if (optionText != null)
        {
            optionText.text = "";
        }

        if (bodyText != null)
        {
            bodyText.text =
                "This visualization uses the publicly " +
                "available Edenhofer et al. (2024) " +
                "three-dimensional dust map of the " +
                "local Milky Way.\n\n" +
                "The map traces interstellar dust near " +
                "the Sun. In this VR version, dust " +
                "density is represented by " +
                "semi-transparent particles so users " +
                "can explore nearby Galactic dust " +
                "structure from inside the data.";
        }
    }

    private void OpenFastTravel()
    {
        if (fastTravelMenu == null)
        {
            Debug.LogError(
                "DesktopMenuController: " +
                "Fast Travel Menu is not assigned."
            );

            return;
        }

        menuOpen = false;

        if (menuCanvas != null)
        {
            menuCanvas.SetActive(false);
        }

        fastTravelMenu.OpenMenu();
    }
}