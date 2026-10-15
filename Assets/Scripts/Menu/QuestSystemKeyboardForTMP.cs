using System.Collections;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TMP_InputField))]
public class QuestSystemKeyboardForTMP : MonoBehaviour
{
    private TMP_InputField inputField;
    private TouchScreenKeyboard keyboard;
    private Coroutine keyboardRoutine;
    private string originalText;

    private void Awake()
    {
        inputField = GetComponent<TMP_InputField>();

#if UNITY_ANDROID && !UNITY_EDITOR
        // We will open and manage the keyboard ourselves.
        inputField.shouldHideSoftKeyboard = true;
#endif

        inputField.onSelect.AddListener(OnInputSelected);
    }

    private void OnDestroy()
    {
        if (inputField != null)
            inputField.onSelect.RemoveListener(OnInputSelected);
    }

    private void OnInputSelected(string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (keyboardRoutine == null)
            keyboardRoutine = StartCoroutine(OpenKeyboard());
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private IEnumerator OpenKeyboard()
    {
        // Let Unity finish selecting the input field.
        yield return null;

        originalText = inputField.text;

        // Meta's system keyboard overlay uses the Default keyboard type.
        keyboard = TouchScreenKeyboard.Open(
            inputField.text,
            TouchScreenKeyboardType.Default
        );

        if (keyboard == null)
        {
            Debug.LogWarning(
                "QuestSystemKeyboardForTMP: Could not open the keyboard."
            );

            keyboardRoutine = null;
            yield break;
        }

        while (keyboard != null &&
               keyboard.status != TouchScreenKeyboard.Status.Done &&
               keyboard.status != TouchScreenKeyboard.Status.Canceled &&
               keyboard.status != TouchScreenKeyboard.Status.LostFocus)
        {
            if (inputField != null &&
                inputField.text != keyboard.text)
            {
                inputField.text = keyboard.text;
            }

            yield return null;
        }

        if (inputField != null && keyboard != null)
        {
            if (keyboard.status == TouchScreenKeyboard.Status.Canceled)
                inputField.text = originalText;
            else
                inputField.text = keyboard.text;

            inputField.DeactivateInputField();
        }

        keyboard = null;
        keyboardRoutine = null;
    }
#endif
}