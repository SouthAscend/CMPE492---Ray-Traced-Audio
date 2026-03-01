using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(VoxelVolume))]
public class VoxelVolumeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        VoxelVolume volume = (VoxelVolume)target;

        if (volume.voxelSize <= 0.01f)
        {
            EditorGUILayout.HelpBox("Voxel size must be greater than 0.01.", MessageType.Error);
            return;
        }

        Vector3 scale = volume.transform.localScale;
        int sizeX = Mathf.CeilToInt(scale.x / volume.voxelSize);
        int sizeY = Mathf.CeilToInt(scale.y / volume.voxelSize);
        int sizeZ = Mathf.CeilToInt(scale.z / volume.voxelSize);
        
        long totalVoxels = (long)sizeX * sizeY * sizeZ;
        float ramMB = totalVoxels / 1048576f;

        EditorGUILayout.HelpBox($"Grid Dimensions: {sizeX} x {sizeY} x {sizeZ}\nTotal Voxels: {totalVoxels}\nEstimated RAM: {ramMB:F3} MB", MessageType.Info);

        if (GUILayout.Button("Build Voxels"))
        {
            BuildVoxels(volume, sizeX, sizeY, sizeZ, totalVoxels);
        }
    }

    private void BuildVoxels(VoxelVolume volume, int sizeX, int sizeY, int sizeZ, long totalVoxels)
    {
        if (totalVoxels > int.MaxValue)
        {
            Debug.LogError("Grid is too large. Exceeds array index limit.");
            return;
        }

        bool shouldCreateVisuals = volume.createVisuals;
        if (shouldCreateVisuals && totalVoxels > 500000)
        {
            Debug.LogWarning($"Visual representation disabled: Total voxels ({totalVoxels}) exceeds the safe limit of 500,000 to prevent editor crashes. The voxel data asset will still be generated.");
            shouldCreateVisuals = false;
        }

        VoxelGridData gridData = ScriptableObject.CreateInstance<VoxelGridData>();
        gridData.gridSize = new Vector3Int(sizeX, sizeY, sizeZ);
        gridData.voxelSize = volume.voxelSize;
        gridData.origin = volume.transform.position - (volume.transform.localScale / 2f);
        gridData.isOccupied = new bool[totalVoxels];

        GameObject visParent = null;
        if (shouldCreateVisuals)
        {
            // destroy the existing visualization
            GameObject existingVis = GameObject.Find($"Voxel_Visualization_{volume.gameObject.name}");
            if (existingVis != null)
            {
                DestroyImmediate(existingVis);
            }
            visParent = new GameObject($"Voxel_Visualization_{volume.gameObject.name}");
            visParent.tag = "EditorOnly";
            visParent.AddComponent<DestroyOnPlay>();
            visParent.transform.position = volume.transform.position;
        }

        Vector3 halfExtents = Vector3.one * (volume.voxelSize / 2f);

        for (int z = 0; z < sizeZ; z++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + (y * sizeX) + (z * sizeX * sizeY);
                    Vector3 center = gridData.origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * volume.voxelSize;

                    bool hit = Physics.CheckBox(center, halfExtents, Quaternion.identity, volume.obstacleMask);
                    gridData.isOccupied[index] = hit;

                    if (hit && shouldCreateVisuals)
                    {
                        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        cube.transform.position = center;
                        cube.transform.localScale = Vector3.one * volume.voxelSize;
                        cube.transform.SetParent(visParent.transform);
                        DestroyImmediate(cube.GetComponent<Collider>());
                    }
                }
            }
        }

        if (!AssetDatabase.IsValidFolder("Assets/VoxelData"))
        {
            AssetDatabase.CreateFolder("Assets", "VoxelData");
        }

        string levelName = SceneManager.GetActiveScene().name;
        string assetName = $"voxel_info_{levelName}";
        string path = $"Assets/VoxelData/{assetName}.asset";

        gridData.name = assetName;

        VoxelGridData existingAsset = AssetDatabase.LoadAssetAtPath<VoxelGridData>(path);
        
        if (existingAsset != null)
        {
            EditorUtility.CopySerialized(gridData, existingAsset);
            AssetDatabase.SaveAssets();
            DestroyImmediate(gridData); 
            volume.voxelData = existingAsset;
        }
        else
        {
            AssetDatabase.CreateAsset(gridData, path);
            AssetDatabase.SaveAssets();
            volume.voxelData = gridData;
        }

        EditorUtility.SetDirty(volume);
        Debug.Log("Voxel build complete.");
    }
}