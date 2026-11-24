using UnityEngine;
using UnityEngine.InputSystem;

public class FlyingCamera : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 10f;
    public float fastSpeed = 30f;
    public float lookSensitivity = 0.1f;

    private float yaw = 0f;
    private float pitch = 0f;
    private bool cursorLocked = true;

    private void Start()
    {
        SetCursorState(true);
    }

    private void Update()
    {
        HandleCursorToggle();
        if (cursorLocked)
        {
            HandleMouseLook();
        }
        HandleMovement();
    }

    private void HandleMouseLook()
    {
        if (Mouse.current != null)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            yaw += mouseDelta.x * lookSensitivity;
            pitch -= mouseDelta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -90f, 90f);

            transform.eulerAngles = new Vector3(pitch, yaw, 0f);
        }
    }

    private void HandleMovement()
    {
        Vector3 direction = Vector3.zero;

        if (Keyboard.current.wKey.isPressed) direction += transform.forward;
        if (Keyboard.current.sKey.isPressed) direction -= transform.forward;
        if (Keyboard.current.aKey.isPressed) direction -= transform.right;
        if (Keyboard.current.dKey.isPressed) direction += transform.right;
        if (Keyboard.current.eKey.isPressed) direction += transform.up;
        if (Keyboard.current.qKey.isPressed) direction -= transform.up;

        float speed = Keyboard.current.leftShiftKey.isPressed ? fastSpeed : moveSpeed;

        transform.position += direction * speed * Time.deltaTime;
    }

    private void HandleCursorToggle()
    {
        // Press Escape to toggle cursor visibility
        if (Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            cursorLocked = !cursorLocked;
            SetCursorState(cursorLocked);
        }
    }

    private void SetCursorState(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
