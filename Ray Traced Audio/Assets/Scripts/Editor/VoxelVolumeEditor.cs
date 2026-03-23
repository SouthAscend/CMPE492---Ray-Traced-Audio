using System.Collections;
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

        float ramMB = totalVoxels / 8f / 1048576f;

        EditorGUILayout.HelpBox(
            $"Grid Dimensions: {sizeX} x {sizeY} x {sizeZ}\n" +
            $"Total Voxels: {totalVoxels}\n" +
            $"Estimated RAM: {ramMB:F3} MB (BitArray packed)",
            MessageType.Info);

        if (GUILayout.Button("Build Voxels"))
        {
            EnsureVoxelDataAsset(volume);

            volume.BakeVoxels();

            // Update the hash so runtime knows this bake is fresh
            volume.voxelData.sceneHash = volume.ComputeSceneHash();

            EditorUtility.SetDirty(volume.voxelData);
            EditorUtility.SetDirty(volume);
            AssetDatabase.SaveAssets();

            Debug.Log("[VoxelVolumeEditor] Manual bake complete.");
        }
    }

    /// <summary>
    /// Creates the VoxelGridData asset if it doesn't exist yet,
    /// and assigns it to the volume. Mirrors the old editor behaviour.
    /// </summary>
    private void EnsureVoxelDataAsset(VoxelVolume volume)
    {
        if (volume.voxelData != null) return;

        if (!AssetDatabase.IsValidFolder("Assets/VoxelData"))
            AssetDatabase.CreateFolder("Assets", "VoxelData");

        string levelName = SceneManager.GetActiveScene().name;
        string path = $"Assets/VoxelData/voxel_info_{levelName}.asset";

        VoxelGridData existing = AssetDatabase.LoadAssetAtPath<VoxelGridData>(path);
        if (existing != null)
        {
            volume.voxelData = existing;
        }
        else
        {
            VoxelGridData newAsset = ScriptableObject.CreateInstance<VoxelGridData>();
            newAsset.name = $"voxel_info_{levelName}";
            AssetDatabase.CreateAsset(newAsset, path);
            AssetDatabase.SaveAssets();
            volume.voxelData = newAsset;
        }

        EditorUtility.SetDirty(volume);
    }
}