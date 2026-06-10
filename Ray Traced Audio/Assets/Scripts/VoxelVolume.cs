using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class VoxelVolume : MonoBehaviour
{
    [Header("Voxel Settings")]
    public float voxelSize = 0.5f;
    public bool createVisuals = true;
    public VoxelGridData voxelData;

    [Header("Layers")]
    [Tooltip("Static geometry — walls, floors, ceilings. Always baked.")]
    public LayerMask staticObstacleMask;

    [Tooltip("Enable to support dynamic obstacles at runtime via DynamismManager.")]
    public bool useDynamicObstacles = false;

    [Tooltip("Dynamic obstacle layer. Only used when useDynamicObstacles is OFF — baked into static grid. " +
             "When ON, dynamic grid is managed separately by DynamismManager.")]
    public LayerMask dynamicObstacleMask;

    [SerializeField] private AudioRaytracer audioRaytracer;

    [Tooltip("When enabled, only bakes a single Y slice at listener height.")]
    public bool use2DMode = false;

    private static readonly string[] ExcludedTags = { "SoundSource", "SoundListener" };

    // -------------------------------------------------------------------------

    private void Start()
    {
        if (voxelData == null)
        {
            Debug.LogError("[VoxelVolume] No VoxelGridData asset assigned. Cannot proceed.");
            return;
        }

        string liveHash = ComputeSceneHash();

        if (voxelData.isOccupiedBits != null &&
            voxelData.isOccupiedBits.Length > 0 &&
            voxelData.sceneHash == liveHash)
        {
            Debug.Log("[VoxelVolume] Scene geometry unchanged. Using cached voxel data.");
        }
        else
        {
            Debug.Log("[VoxelVolume] Scene geometry changed or no cache found. Rebaking...");
            BakeVoxels();
            voxelData.sceneHash = liveHash;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(voxelData);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
            Debug.Log("[VoxelVolume] Bake complete.");
        }

        if (useDynamicObstacles)
        {
            // Hand static grid to DynamismManager — dynamic grid managed separately
            if (DynamismManager.Instance != null)
                DynamismManager.Instance.InitializeStatic(voxelData);
            else
                Debug.LogWarning("[VoxelVolume] useDynamicObstacles is ON but no DynamismManager found in scene.");

            // AudioRaytracer reads combined bits from DynamismManager each cycle
            audioRaytracer._use2DMode = use2DMode;
            audioRaytracer.InitializeDynamic(voxelData);
        }
        else
        {
            // Static only — pass BitArray directly, no DynamismManager involved
            BitArray staticBits = voxelData.GetBitArray();
            audioRaytracer._use2DMode = use2DMode;
            audioRaytracer.InitializeStatic(voxelData, staticBits);
        }
    }

    // -------------------------------------------------------------------------

    public void BakeVoxels()
    {
        Vector3 scale = transform.localScale;
        int sizeX = Mathf.CeilToInt(scale.x / voxelSize);
        int sizeZ = Mathf.CeilToInt(scale.z / voxelSize);

        int sizeY;
        int sliceY = 0;

        if (use2DMode)
        {
            sizeY = 1;
            GameObject listenerObj = GameObject.FindWithTag("SoundListener");
            if (listenerObj == null)
            {
                Debug.LogError("[VoxelVolume] 2D mode requires a SoundListener tagged object.");
                return;
            }
            Vector3 origin2D = transform.position - (transform.localScale / 2f);
            sliceY = Mathf.FloorToInt((listenerObj.transform.position.y - origin2D.y) / voxelSize);
            sliceY = Mathf.Clamp(sliceY, 0, Mathf.CeilToInt(scale.y / voxelSize) - 1);
        }
        else
        {
            sizeY = Mathf.CeilToInt(scale.y / voxelSize);
        }

        long totalVoxels = (long)sizeX * sizeY * sizeZ;

        if (totalVoxels > int.MaxValue)
        {
            Debug.LogError("[VoxelVolume] Grid too large — exceeds array index limit.");
            return;
        }

        List<Collider> disabledColliders = DisableExcludedColliders();

        Vector3 gridOrigin = transform.position - (transform.localScale / 2f);

        voxelData.gridSize = new Vector3Int(sizeX, sizeY, sizeZ);
        voxelData.voxelSize = voxelSize;
        voxelData.origin = use2DMode
            ? new Vector3(gridOrigin.x, gridOrigin.y + sliceY * voxelSize, gridOrigin.z)
            : gridOrigin;

        LayerMask bakeMask = useDynamicObstacles
            ? staticObstacleMask
            : staticObstacleMask | dynamicObstacleMask;

        bool[] tempOccupied = new bool[(int)totalVoxels];
        Vector3 halfExtents = Vector3.one * (voxelSize / 2f);

        if (use2DMode)
        {
            for (int z = 0; z < sizeZ; z++)
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + 0 * sizeX + z * sizeX * 1;
                    Vector3 center = gridOrigin +
                        new Vector3(x + 0.5f, sliceY + 0.5f, z + 0.5f) * voxelSize;

                    tempOccupied[index] = Physics.CheckBox(
                        center, halfExtents, Quaternion.identity, bakeMask);
                }
        }
        else
        {
            for (int z = 0; z < sizeZ; z++)
                for (int y = 0; y < sizeY; y++)
                    for (int x = 0; x < sizeX; x++)
                    {
                        int index = x + y * sizeX + z * sizeX * sizeY;
                        Vector3 center = voxelData.origin +
                            new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * voxelSize;

                        tempOccupied[index] = Physics.CheckBox(
                            center, halfExtents, Quaternion.identity, bakeMask);
                    }
        }

        RestoreColliders(disabledColliders);

        BitArray bits = new BitArray(tempOccupied);
        voxelData.isOccupiedBits = new int[Mathf.CeilToInt((int)totalVoxels / 32f)];
        bits.CopyTo(voxelData.isOccupiedBits, 0);
    }

    // -------------------------------------------------------------------------

    public string ComputeSceneHash()
    {
        Vector3 gridCenter = transform.position;
        Vector3 gridHalfExtents = transform.localScale / 2f;

        // Hash includes both layers regardless of mode —
        // any geometry change in either layer should invalidate the cache
        LayerMask hashMask = useDynamicObstacles
            ? staticObstacleMask
            : staticObstacleMask | dynamicObstacleMask;

        Collider[] colliders = Physics.OverlapBox(
            gridCenter, gridHalfExtents, Quaternion.identity, hashMask);

        List<Collider> sorted = new List<Collider>(colliders);
        sorted.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

        HashSet<string> excludedTags = new HashSet<string>(ExcludedTags);

        StringBuilder sb = new StringBuilder();
        foreach (Collider col in sorted)
        {
            if (excludedTags.Contains(col.gameObject.tag)) continue;

            Bounds b = col.bounds;
            sb.Append(b.center.x).Append(',')
              .Append(b.center.y).Append(',')
              .Append(b.center.z).Append(',')
              .Append(b.size.x).Append(',')
              .Append(b.size.y).Append(',')
              .Append(b.size.z).Append(',')
              .Append(voxelSize).Append(';');
        }

        return sb.ToString().GetHashCode().ToString();
    }

    // -------------------------------------------------------------------------

    private List<Collider> DisableExcludedColliders()
    {
        List<Collider> disabled = new List<Collider>();
        foreach (string tag in ExcludedTags)
            foreach (GameObject obj in GameObject.FindGameObjectsWithTag(tag))
                foreach (Collider col in obj.GetComponentsInChildren<Collider>())
                {
                    if (col.enabled)
                    {
                        col.enabled = false;
                        disabled.Add(col);
                    }
                }
        return disabled;
    }

    private void RestoreColliders(List<Collider> colliders)
    {
        foreach (Collider col in colliders)
            if (col != null) col.enabled = true;
    }

    // -------------------------------------------------------------------------

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.3f);
        Gizmos.DrawCube(transform.position, transform.localScale);
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(transform.position, transform.localScale);
    }
}