using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class VoxelVolume : MonoBehaviour
{
    public float voxelSize = 0.5f;
    public LayerMask obstacleMask;
    public bool createVisuals = true;
    public VoxelGridData voxelData;
    [SerializeField] private AudioRaytracer audioRaytracer;

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

        // Hand static grid to DynamismManager — it will also pick up any
        // DynamicCubeObstacle components already in the scene
        if (DynamismManager.Instance != null)
            DynamismManager.Instance.InitializeStatic(voxelData);
        else
            Debug.LogWarning("[VoxelVolume] No DynamismManager found in scene. Dynamic obstacles will not work.");

        // Initialize AudioRaytracer — pass voxelData for grid dimensions
        audioRaytracer.Initialize(voxelData);
    }

    // -------------------------------------------------------------------------

    public void BakeVoxels()
    {
        Vector3 scale = transform.localScale;
        int sizeX = Mathf.CeilToInt(scale.x / voxelSize);
        int sizeY = Mathf.CeilToInt(scale.y / voxelSize);
        int sizeZ = Mathf.CeilToInt(scale.z / voxelSize);
        long totalVoxels = (long)sizeX * sizeY * sizeZ;

        if (totalVoxels > int.MaxValue)
        {
            Debug.LogError("[VoxelVolume] Grid too large — exceeds array index limit.");
            return;
        }

        List<Collider> disabledColliders = DisableExcludedColliders();

        voxelData.gridSize = new Vector3Int(sizeX, sizeY, sizeZ);
        voxelData.voxelSize = voxelSize;
        voxelData.origin = transform.position - (transform.localScale / 2f);

        bool[] tempOccupied = new bool[(int)totalVoxels];
        Vector3 halfExtents = Vector3.one * (voxelSize / 2f);

        for (int z = 0; z < sizeZ; z++)
        for (int y = 0; y < sizeY; y++)
        for (int x = 0; x < sizeX; x++)
        {
            int index = x + y * sizeX + z * sizeX * sizeY;
            Vector3 center = voxelData.origin +
                new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * voxelSize;

            tempOccupied[index] = Physics.CheckBox(
                center, halfExtents, Quaternion.identity, obstacleMask);
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

        Collider[] colliders = Physics.OverlapBox(
            gridCenter, gridHalfExtents, Quaternion.identity, obstacleMask);

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
              .Append(b.size.z).Append(';');
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