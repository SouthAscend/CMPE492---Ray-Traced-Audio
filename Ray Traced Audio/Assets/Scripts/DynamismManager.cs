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
/// Thread-safe: AudioRaytracer background thread reads combined via SnapshotCombinedBits().
/// </summary>
public class DynamismManager : MonoBehaviour
{
    public static DynamismManager Instance { get; private set; }

    private VoxelGridData _grid;

    // Static grid — defensive copy of baked int[], never modified
    private int[] _staticInts;

    // Dynamic grid — modified via OBB math at runtime
    private int[] _dynamicInts;

    // Combined = static OR dynamic
    private int[] _combinedInts;
    private BitArray _combinedBits;
    private readonly object _combinedLock = new object();
    private bool _combinedDirty = false;

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
    /// Called by VoxelVolume after bake when useDynamicObstacles is ON.
    /// Makes a defensive copy of the static int[] so external changes
    /// to the asset never corrupt our working data.
    /// </summary>
    public void InitializeStatic(VoxelGridData grid)
    {
        _grid = grid;

        int intCount = grid.isOccupiedBits.Length;

        // Defensive copy — _staticInts is fully independent of the asset
        _staticInts = new int[intCount];
        System.Array.Copy(grid.isOccupiedBits, _staticInts, intCount);

        _dynamicInts = new int[intCount];
        _combinedInts = new int[intCount];

        // Initial combined = static only (dynamic is empty)
        System.Array.Copy(_staticInts, _combinedInts, intCount);

        // Build initial combined BitArray — must be ready before first snapshot
        lock (_combinedLock)
        {
            _combinedBits = new BitArray(_combinedInts);
        }

        // Find any DynamicCubeObstacles already in the scene
        DynamicCubeObstacle[] found = FindObjectsByType<DynamicCubeObstacle>(FindObjectsSortMode.None);
        foreach (IDynamicObstacle obs in found)
            RegisterDynamic(obs);

        Debug.Log($"[DynamismManager] Initialized. {_dynamicEntries.Count} dynamic obstacles registered.");
    }

    // -------------------------------------------------------------------------

    public void RegisterDynamic(IDynamicObstacle obstacle)
    {
        if (_grid == null) return;

        foreach (DynamicEntry e in _dynamicEntries)
            if (e.obstacle == obstacle) return;

        DynamicEntry entry = new DynamicEntry
        {
            obstacle = obstacle,
            lastBounds = obstacle.GetWorldBounds()
        };

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

            MonoBehaviour mb = entry.obstacle as MonoBehaviour;
            if (mb == null || !mb.transform.hasChanged) continue;

            SetDynamicBits(entry.occupiedIndices, false);

            entry.occupiedIndices = entry.obstacle.GetOccupiedVoxelIndices(_grid);
            entry.lastBounds = entry.obstacle.GetWorldBounds();

            SetDynamicBits(entry.occupiedIndices, true);

            mb.transform.hasChanged = false;
            _combinedDirty = true;
        }

        if (_combinedDirty)
        {
            RebuildCombined();
            _combinedDirty = false;
        }
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Thread-safe snapshot for AudioRaytracer background thread.
    /// Copies under lock so the background thread reads a consistent state.
    /// </summary>
    public BitArray SnapshotCombinedBits()
    {
        lock (_combinedLock)
        {
            return new BitArray(_combinedBits);
        }
    }

    // -------------------------------------------------------------------------

    private void RebuildCombined()
    {
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