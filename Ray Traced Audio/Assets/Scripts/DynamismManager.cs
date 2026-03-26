using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Singleton that manages two separate voxel grids:
///
///   Static grid  — baked from scene geometry, never changes at runtime
///   Dynamic grid — updated via Burst OBB jobs when dynamic obstacles change
///
/// Optimizations:
///   - Latest-wins pending snapshot — intermediate transform states are discarded
///   - Non-blocking jobs — IsCompleted checked each frame, never blocks main thread
///   - Dirty-region combined rebuild — only OR the int slots that actually changed,
///     not the entire grid array
///   - No same-frame rescheduling — after a job completes, pending snapshot is
///     picked up next frame, preventing back-to-back SetDynamicBits calls
/// </summary>
public class DynamismManager : MonoBehaviour
{
    public static DynamismManager Instance { get; private set; }

    private VoxelGridData _grid;
    private int[] _staticInts;
    private int[] _dynamicInts;
    private int[] _combinedInts;
    private BitArray _combinedBits;
    private readonly object _combinedLock = new object();

    // -------------------------------------------------------------------------

    private struct TransformSnapshot
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 lossyScale;
    }

    private class DynamicEntry
    {
        public IDynamicObstacle obstacle;
        public List<int> lastCompletedIndices = new List<int>();

        public bool hasPendingSnapshot = false;
        public TransformSnapshot pendingSnapshot;

        public bool jobInFlight = false;
        public JobHandle jobHandle;
        public NativeArray<int3> jobVoxelCoords;
        public NativeArray<byte> jobResults;
        public List<int> jobFlatIndices;
        public int rescheduleCountdown = 0;
    }

    private List<DynamicEntry> _entries = new List<DynamicEntry>();

    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void InitializeStatic(VoxelGridData grid)
    {
        _grid = grid;

        int intCount = grid.isOccupiedBits.Length;
        _staticInts = new int[intCount];
        System.Array.Copy(grid.isOccupiedBits, _staticInts, intCount);

        _dynamicInts = new int[intCount];
        _combinedInts = new int[intCount];
        System.Array.Copy(_staticInts, _combinedInts, intCount);

        lock (_combinedLock)
            _combinedBits = new BitArray(_combinedInts);

        DynamicCubeObstacle[] found = FindObjectsByType<DynamicCubeObstacle>(FindObjectsSortMode.None);
        foreach (IDynamicObstacle obs in found)
            RegisterDynamic(obs);

        Debug.Log($"[DynamismManager] Initialized. {_entries.Count} dynamic obstacles registered.");
    }

    // -------------------------------------------------------------------------

    public void RegisterDynamic(IDynamicObstacle obstacle)
    {
        if (_grid == null) return;
        foreach (DynamicEntry e in _entries)
            if (e.obstacle == obstacle) return;

        DynamicEntry entry = new DynamicEntry { obstacle = obstacle };

        MonoBehaviour mb = obstacle as MonoBehaviour;
        if (mb != null)
        {
            entry.hasPendingSnapshot = true;
            entry.pendingSnapshot = SnapshotTransform(mb.transform);
            mb.transform.hasChanged = false;
        }

        _entries.Add(entry);
    }

    public void UnregisterDynamic(IDynamicObstacle obstacle)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            DynamicEntry entry = _entries[i];
            if (entry.obstacle != obstacle) continue;

            if (entry.jobInFlight)
            {
                entry.jobHandle.Complete();
                entry.jobVoxelCoords.Dispose();
                entry.jobResults.Dispose();
            }

            // Clear voxels and rebuild only the affected region
            if (entry.lastCompletedIndices.Count > 0)
            {
                SetDynamicBits(entry.lastCompletedIndices, false);
                RebuildDirtyRegion(entry.lastCompletedIndices);
            }

            _entries.RemoveAt(i);
            return;
        }
    }

    // -------------------------------------------------------------------------

    private void Update()
    {
        if (_grid == null) return;

        foreach (DynamicEntry entry in _entries)
        {
            MonoBehaviour mb = entry.obstacle as MonoBehaviour;
            if (mb == null) continue;

            // Always overwrite pending with latest transform — intermediate states discarded
            if (mb.transform.hasChanged)
            {
                entry.hasPendingSnapshot = true;
                entry.pendingSnapshot = SnapshotTransform(mb.transform);
                mb.transform.hasChanged = false;
            }

            // Check in-flight job
            if (entry.jobInFlight)
            {
                if (!entry.jobHandle.IsCompleted) continue;

                // Job done — collect results
                entry.jobHandle.Complete();

                List<int> newIndices = new List<int>();
                for (int i = 0; i < entry.jobResults.Length; i++)
                    if (entry.jobResults[i] == 1)
                        newIndices.Add(entry.jobFlatIndices[i]);

                entry.jobVoxelCoords.Dispose();
                entry.jobResults.Dispose();
                entry.jobInFlight = false;

                // Compute dirty int slots = union of old and new indices
                HashSet<int> dirtyIntSlots = new HashSet<int>();
                foreach (int idx in entry.lastCompletedIndices) dirtyIntSlots.Add(idx / 32);
                foreach (int idx in newIndices) dirtyIntSlots.Add(idx / 32);

                // Clear old, fill new
                SetDynamicBits(entry.lastCompletedIndices, false);
                SetDynamicBits(newIndices, true);
                entry.lastCompletedIndices = newIndices;

                // Rebuild only the dirty int slots — not the entire array
                RebuildDirtySlots(dirtyIntSlots);

                // Do NOT reschedule this frame — pending snapshot picked up next frame
                // This prevents back-to-back SetDynamicBits calls in the same frame
                entry.rescheduleCountdown = 5;
                continue;
            }

            // No job in flight — schedule pending snapshot if one exists
            if (entry.hasPendingSnapshot)
            {
                if (entry.rescheduleCountdown > 0)
                    entry.rescheduleCountdown--;
                else
                    ScheduleJob(entry);
            }
        }
    }

    // -------------------------------------------------------------------------

    private void ScheduleJob(DynamicEntry entry)
    {
        TransformSnapshot snap = entry.pendingSnapshot;
        entry.hasPendingSnapshot = false;

        Bounds worldBounds = ComputeAABB(snap);

        Vector3Int min = WorldToVoxel(worldBounds.min);
        Vector3Int max = WorldToVoxel(worldBounds.max);

        min.x = Mathf.Clamp(min.x, 0, _grid.gridSize.x - 1);
        min.y = Mathf.Clamp(min.y, 0, _grid.gridSize.y - 1);
        min.z = Mathf.Clamp(min.z, 0, _grid.gridSize.z - 1);
        max.x = Mathf.Clamp(max.x + 1, 0, _grid.gridSize.x - 1);
        max.y = Mathf.Clamp(max.y + 1, 0, _grid.gridSize.y - 1);
        max.z = Mathf.Clamp(max.z + 1, 0, _grid.gridSize.z - 1);

        List<int3> coordList = new List<int3>();
        List<int> indexList = new List<int>();

        for (int z = min.z; z <= max.z; z++)
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            coordList.Add(new int3(x, y, z));
            indexList.Add(x + y * _grid.gridSize.x + z * _grid.gridSize.x * _grid.gridSize.y);
        }

        if (coordList.Count == 0) return;

        NativeArray<int3> voxelCoords = new NativeArray<int3>(coordList.ToArray(), Allocator.Persistent);
        NativeArray<byte> results = new NativeArray<byte>(coordList.Count, Allocator.Persistent);

        Quaternion rotInv = Quaternion.Inverse(snap.rotation);

        OBBVoxelFillJob job = new OBBVoxelFillJob
        {
            obbCenter = snap.position,
            obbRotationInverse = new quaternion(rotInv.x, rotInv.y, rotInv.z, rotInv.w),
            obbHalfExtents = new float3(
                snap.lossyScale.x * 0.5f,
                snap.lossyScale.y * 0.5f,
                snap.lossyScale.z * 0.5f),
            gridOrigin = _grid.origin,
            voxelSize = _grid.voxelSize,
            voxelCoords = voxelCoords,
            results = results
        };

        entry.jobHandle = job.Schedule(coordList.Count, 64);
        entry.jobVoxelCoords = voxelCoords;
        entry.jobResults = results;
        entry.jobFlatIndices = indexList;
        entry.jobInFlight = true;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds only the combined int slots that were touched by this update.
    /// For a door-sized object this is ~200 slots instead of ~105,000.
    /// </summary>
    private void RebuildDirtySlots(HashSet<int> dirtyIntSlots)
    {
        lock (_combinedLock)
        {
            foreach (int slot in dirtyIntSlots)
                _combinedInts[slot] = _staticInts[slot] | _dynamicInts[slot];

            // Reconstruct BitArray from updated combined ints
            // Only the changed slots affect the result
            _combinedBits = new BitArray(_combinedInts);
        }
    }

    /// <summary>
    /// Used when an obstacle is unregistered — rebuilds only its region.
    /// </summary>
    private void RebuildDirtyRegion(List<int> indices)
    {
        HashSet<int> slots = new HashSet<int>();
        foreach (int idx in indices) slots.Add(idx / 32);
        RebuildDirtySlots(slots);
    }

    // -------------------------------------------------------------------------

    public BitArray SnapshotCombinedBits()
    {
        lock (_combinedLock)
            return new BitArray(_combinedBits);
    }

    // -------------------------------------------------------------------------

    private void SetDynamicBits(List<int> indices, bool value)
    {
        foreach (int index in indices)
        {
            int intIdx = index / 32;
            int bitIdx = index % 32;
            if (value)
                _dynamicInts[intIdx] |= (1 << bitIdx);
            else
                _dynamicInts[intIdx] &= ~(1 << bitIdx);
        }
    }

    private TransformSnapshot SnapshotTransform(Transform t)
    {
        return new TransformSnapshot
        {
            position = t.position,
            rotation = t.rotation,
            lossyScale = t.lossyScale
        };
    }

    private Bounds ComputeAABB(TransformSnapshot snap)
    {
        Vector3 half = snap.lossyScale * 0.5f;
        Vector3 right   = snap.rotation * new Vector3(half.x, 0, 0);
        Vector3 up      = snap.rotation * new Vector3(0, half.y, 0);
        Vector3 forward = snap.rotation * new Vector3(0, 0, half.z);

        Vector3 absExtents = new Vector3(
            Mathf.Abs(right.x) + Mathf.Abs(up.x) + Mathf.Abs(forward.x),
            Mathf.Abs(right.y) + Mathf.Abs(up.y) + Mathf.Abs(forward.y),
            Mathf.Abs(right.z) + Mathf.Abs(up.z) + Mathf.Abs(forward.z));

        return new Bounds(snap.position, absExtents * 2f);
    }

    private Vector3Int WorldToVoxel(Vector3 worldPos)
    {
        Vector3 local = worldPos - _grid.origin;
        return new Vector3Int(
            Mathf.FloorToInt(local.x / _grid.voxelSize),
            Mathf.FloorToInt(local.y / _grid.voxelSize),
            Mathf.FloorToInt(local.z / _grid.voxelSize));
    }

    // -------------------------------------------------------------------------

    private void OnDestroy()
    {
        foreach (DynamicEntry entry in _entries)
        {
            if (entry.jobInFlight)
            {
                entry.jobHandle.Complete();
                entry.jobVoxelCoords.Dispose();
                entry.jobResults.Dispose();
            }
        }
    }
}