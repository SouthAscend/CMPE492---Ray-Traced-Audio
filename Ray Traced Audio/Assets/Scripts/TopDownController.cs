using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simple top-down WASD controller using the new Unity Input System.
/// Movement is relative to the camera's orientation, Y component flattened to 0.
/// No physics, no gravity, no acceleration.
/// Uses Physics.CheckSphere to prevent moving into solid geometry.
/// </summary>
public class TopDownController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;

    [Header("Camera")]
    [Tooltip("The camera to use for direction reference. Defaults to Camera.main if not assigned.")]
    public Camera referenceCamera;

    [Header("Collision")]
    [Tooltip("Radius of the collision check sphere. Should match roughly half the object's width.")]
    public float collisionRadius = 0.3f;
    public LayerMask collisionMask;

    private void Start()
    {
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

        // Get camera forward and right, flattened to the horizontal plane
        Vector3 camForward = referenceCamera.transform.forward;
        Vector3 camRight = referenceCamera.transform.right;

        camForward.y = 0f;
        camRight.y = 0f;

        camForward.Normalize();
        camRight.Normalize();

        Vector3 direction = (camForward * v + camRight * h).normalized;

        Vector3 nextPosition = transform.position + direction * moveSpeed * Time.deltaTime;

        if (!Physics.CheckSphere(nextPosition, collisionRadius, collisionMask))
        {
            transform.position = nextPosition;
        }
    }
}