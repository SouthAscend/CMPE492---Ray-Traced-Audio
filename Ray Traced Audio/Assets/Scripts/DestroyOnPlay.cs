using UnityEngine;

public class DestroyOnPlay : MonoBehaviour
{
    private void Awake()
    {
        Destroy(gameObject);
    }
}