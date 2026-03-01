using UnityEngine;

[CreateAssetMenu(fileName = "NewVoxelGrid", menuName = "RaytracedAudio/VoxelGridData")]
public class VoxelGridData : ScriptableObject
{
    public Vector3Int gridSize;
    public float voxelSize;
    public Vector3 origin;
    [HideInInspector]
    public bool[] isOccupied;
}