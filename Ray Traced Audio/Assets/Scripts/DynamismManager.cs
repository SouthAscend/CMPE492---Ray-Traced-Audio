using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Singleton that manages two separate voxel grids:
///
///   Static grid  — baked from scene geometry, never changes at runtime
///   Dynamic grid — updated via OBB math when DynamicCubeObstacle transforms change
///
/// Produces a combined grid (static OR dynamic) for AudioRaytracer.
/// Combined is only rebuilt when the dynamic grid actually changes.
///
/// Thread-safe: AudioRaytracer background thread reads combined via ReadCombinedBits().
/// </summary>
public class DynamismManager : MonoBehaviour
{
    public static DynamismManager Instance { get; private set; }

    // Set by VoxelVolume after bake — grid dimensions shared by both grids
    private VoxelGridData _grid;

    // Static grid — int[] from baked asset, read-only, never touched at runtime
    private int[] _staticInts;

    // Dynamic grid — int[] maintained via OBB math
    private int[] _dynamicInts;

    // Combined = static OR dynamic — rebuilt when dynamic changes
    private int[] _combinedInts;
    private BitArray _combinedBits;
    private readonly object _combinedLock = new object();
    private bool _combinedDirty = false;

    // Registered dynamic obstacles and their last known voxel occupancy
    private class DynamicEntry
    {
        public IDynamicObstacle obstacle;
        public List<int> occupiedIndices = new List<int>();
        public Bounds lastBounds;
    }

    private List<DynamicEntry> _dynamicEntries = new List<DynamicEntry>();

    // -------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Called by VoxelVolume after bake. Initializes both grids and
    /// scans the scene for any DynamicCubeObstacle components.
    /// </summary>
    public void InitializeStatic(VoxelGridData grid)
    {
        _grid = grid;
        _staticInts = grid.isOccupiedBits;

        int intCount = _staticInts.Length;
        _dynamicInts = new int[intCount];
        _combinedInts = new int[intCount];

        // Initial combined is just the static grid (dynamic is empty)
        System.Array.Copy(_staticInts, _combinedInts, intCount);

        lock (_combinedLock)
        {
            _combinedBits = new BitArray(_combinedInts);
        }

        // Find any DynamicCubeObstacles already in the scene
        IDynamicObstacle[] found = FindObjectsOfType<DynamicCubeObstacle>();
        foreach (IDynamicObstacle obs in found)
            RegisterDynamic(obs);

        Debug.Log($"[DynamismManager] Initialized. {_dynamicEntries.Count} dynamic obstacles registered.");
    }

    // -------------------------------------------------------------------------

    public void RegisterDynamic(IDynamicObstacle obstacle)
    {
        if (_grid == null) return; // not initialized yet — OnEnable fired too early

        // Avoid duplicates
        foreach (DynamicEntry e in _dynamicEntries)
            if (e.obstacle == obstacle) return;

        DynamicEntry entry = new DynamicEntry
        {
            obstacle = obstacle,
            lastBounds = obstacle.GetWorldBounds()
        };

        // Immediately fill voxels for this obstacle
        entry.occupiedIndices = obstacle.GetOccupiedVoxelIndices(_grid);
        SetDynamicBits(entry.occupiedIndices, true);

        _dynamicEntries.Add(entry);
        _combinedDirty = true;
    }

    public void UnregisterDynamic(IDynamicObstacle obstacle)
    {
        for (int i = _dynamicEntries.Count - 1; i >= 0; i--)
        {
            if (_dynamicEntries[i].obstacle == obstacle)
            {
                // Clear voxels this obstacle was occupying
                SetDynamicBits(_dynamicEntries[i].occupiedIndices, false);
                _dynamicEntries.RemoveAt(i);
                _combinedDirty = true;
                return;
            }
        }
    }

    // -------------------------------------------------------------------------

    private void Update()
    {
        if (_grid == null) return;

        for (int i = 0; i < _dynamicEntries.Count; i++)
        {
            DynamicEntry entry = _dynamicEntries[i];

            // Cast to MonoBehaviour to access hasChanged
            // IDynamicObstacle is always a MonoBehaviour in practice
            MonoBehaviour mb = entry.obstacle as MonoBehaviour;
            if (mb == null || !mb.transform.hasChanged) continue;

            // Clear old voxels
            SetDynamicBits(entry.occupiedIndices, false);

            // Compute new voxels
            entry.occupiedIndices = entry.obstacle.GetOccupiedVoxelIndices(_grid);
            entry.lastBounds = entry.obstacle.GetWorldBounds();

            // Fill new voxels
            SetDynamicBits(entry.occupiedIndices, true);

            mb.transform.hasChanged = false;
            _combinedDirty = true;
        }

        // Rebuild combined only when something actually changed
        if (_combinedDirty)
        {
            RebuildCombined();
            _combinedDirty = false;
        }
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Thread-safe read accessor for AudioRaytracer background thread.
    /// Takes a snapshot copy so the lock is held for the minimum time.
    /// </summary>
    public BitArray SnapshotCombinedBits()
    {
        lock (_combinedLock)
        {
            // Return a copy so the background thread can read freely without holding the lock
            return new BitArray(_combinedBits);
        }
    }

    // -------------------------------------------------------------------------

    private void RebuildCombined()
    {
        // Bitwise OR at the int level — 32 voxels per operation
        for (int i = 0; i < _combinedInts.Length; i++)
            _combinedInts[i] = _staticInts[i] | _dynamicInts[i];

        lock (_combinedLock)
        {
            _combinedBits = new BitArray(_combinedInts);
        }
    }

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
}