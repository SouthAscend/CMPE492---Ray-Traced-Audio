using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Top-down WASD controller using CharacterController.
/// Movement is relative to the camera's orientation, Y component flattened to 0.
/// CharacterController handles wall sliding naturally — no manual collision checks needed.
/// No gravity, no forces, no acceleration.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class TopDownController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;

    [Header("Camera")]
    [Tooltip("The camera to use for direction reference. Defaults to Camera.main if not assigned.")]
    public Camera referenceCamera;

    private CharacterController _cc;

    private void Start()
    {
        _cc = GetComponent<CharacterController>();

        if (referenceCamera == null)
            referenceCamera = Camera.main;
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        float h = 0f;
        float v = 0f;

        if (kb.aKey.isPressed) h -= 1f;
        if (kb.dKey.isPressed) h += 1f;
        if (kb.sKey.isPressed) v -= 1f;
        if (kb.wKey.isPressed) v += 1f;

        if (h == 0f && v == 0f) return;

        Vector3 camForward = referenceCamera.transform.forward;
        Vector3 camRight = referenceCamera.transform.right;

        camForward.y = 0f;
        camRight.y = 0f;

        camForward.Normalize();
        camRight.Normalize();

        Vector3 direction = (camForward * v + camRight * h).normalized;

        // CharacterController.Move handles wall sliding internally
        _cc.Move(direction * moveSpeed * Time.deltaTime);
    }
}