using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Fires rays from the listener through the combined voxel grid on a background thread.
///
/// Threading model:
/// - Main thread copies source/listener positions each frame and signals new work
/// - Background thread snapshots the combined BitArray from DynamismManager,
///   runs the full DDA raytrace, writes result to volatile float
/// - Main thread reads result and applies it to the Audio Mixer low-pass filter
///
/// Combined grid = static baked geometry OR dynamic obstacles (managed by DynamismManager).
/// Muffle Ratio: 0.0 = fully clear, 1.0 = fully blocked.
/// </summary>
public class AudioRaytracer : MonoBehaviour
{
    // Set by VoxelVolume.Initialize() — needed for grid dimensions in DDA
    private VoxelGridData _voxelData;

    [Header("Scene Tags")]
    public string sourceTag = "SoundSource";
    public string listenerTag = "SoundListener";

    [Header("Ray Settings")]
    [Tooltip("Number of rays fired per update.")]
    public int rayCount = 512;

    [Tooltip("How many times a ray can bounce before being discarded.")]
    public int maxBounces = 5;

    [Tooltip("Max voxel steps per ray segment.")]
    public int maxStepsPerSegment = 500;

    [Header("Audio Settings")]
    public AudioMixer audioMixer;

    // -------------------------------------------------------------------------
    // Shared state between main thread and background thread

    private Vector3 _sourcePos;
    private Vector3 _listenerPos;
    private readonly object _posLock = new object();

    private volatile float _muffleRatio = 1f;
    private readonly SemaphoreSlim _workSignal = new SemaphoreSlim(0, 1);
    private volatile bool _shutdown = false;

    // -------------------------------------------------------------------------

    private Transform _source;
    private Transform _listener;
    private Vector3[] _rayDirections;
    private Thread _workerThread;

    // -------------------------------------------------------------------------

    private void Start()
    {
        GameObject sourceObj = GameObject.FindWithTag(sourceTag);
        GameObject listenerObj = GameObject.FindWithTag(listenerTag);

        if (sourceObj == null)
            Debug.LogError($"[AudioRaytracer] No GameObject found with tag '{sourceTag}'.");
        else
            _source = sourceObj.transform;

        if (listenerObj == null)
            Debug.LogError($"[AudioRaytracer] No GameObject found with tag '{listenerTag}'.");
        else
            _listener = listenerObj.transform;
    }

    /// <summary>
    /// Called by VoxelVolume after the static grid is ready.
    /// voxelData is needed for grid dimensions — bits come from DynamismManager.
    /// </summary>
    public void Initialize(VoxelGridData voxelData)
    {
        _voxelData = voxelData;
        _rayDirections = GenerateFibonacciSphere(rayCount);

        _workerThread = new Thread(RaytraceWorker);
        _workerThread.IsBackground = true;
        _workerThread.Start();
    }

    private void Update()
    {
        if (_source == null || _listener == null) return;

        lock (_posLock)
        {
            _sourcePos = _source.position;
            _listenerPos = _listener.position;
        }

        if (_workSignal.CurrentCount == 0)
            _workSignal.Release();

        float muffle = _muffleRatio;
        int successCount = Mathf.RoundToInt((1f - muffle) * rayCount);

        float cutoff = Mathf.Lerp(500f, 22000f, 1f - muffle);
        audioMixer.SetFloat("MuffleFrequency", cutoff);

        Debug.Log($"[AudioRaytracer] Muffle: {muffle:F3} | " +
                  $"Rays succeeded: {successCount}/{rayCount} | " +
                  $"Clarity: {(1f - muffle) * 100f:F1}%");
    }

    private void OnDestroy()
    {
        _shutdown = true;
        try { _workSignal.Release(); } catch { }
    }

    // -------------------------------------------------------------------------
    // Background thread

    private void RaytraceWorker()
    {
        while (true)
        {
            _workSignal.Wait();
            if (_shutdown) break;

            Vector3 sourcePos, listenerPos;
            lock (_posLock)
            {
                sourcePos = _sourcePos;
                listenerPos = _listenerPos;
            }

            // Snapshot the combined grid — holds lock only for the copy duration,
            // not the entire raytrace
            BitArray voxelBits = DynamismManager.Instance != null
                ? DynamismManager.Instance.SnapshotCombinedBits()
                : null;

            if (voxelBits == null || _voxelData == null) continue;

            _muffleRatio = CalculateMuffleRatio(sourcePos, listenerPos, voxelBits);
        }
    }

    // -------------------------------------------------------------------------

    private float CalculateMuffleRatio(Vector3 sourcePos, Vector3 listenerPos, BitArray voxelBits)
    {
        int successCount = 0;

        for (int i = 0; i < _rayDirections.Length; i++)
        {
            Vector3 direction = _rayDirections[i];
            Vector3 currentOrigin = listenerPos;

            for (int bounce = 0; bounce <= maxBounces; bounce++)
            {
                DDATraversal.MarchResult march = DDATraversal.March(
                    currentOrigin,
                    direction,
                    _voxelData,
                    voxelBits,
                    maxStepsPerSegment);

                if (!march.hitSolid)
                    break;

                Vector3 losOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.6f);

                bool hasLOS = DDATraversal.HasLineOfSight(
                    losOrigin,
                    sourcePos,
                    _voxelData,
                    voxelBits,
                    maxStepsPerSegment);

                if (hasLOS)
                {
                    successCount++;
                    break;
                }

                if (bounce < maxBounces)
                {
                    direction = Vector3.Reflect(direction, march.normal);
                    currentOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.1f);
                }
            }
        }

        return 1f - (float)successCount / rayCount;
    }

    // -------------------------------------------------------------------------

    private Vector3[] GenerateFibonacciSphere(int n)
    {
        Vector3[] directions = new Vector3[n];
        float goldenAngle = 2.399963f;

        for (int i = 0; i < n; i++)
        {
            float y = 1f - ((float)i / (n - 1)) * 2f;
            float r = Mathf.Sqrt(1f - y * y);
            float theta = goldenAngle * i;

            directions[i] = new Vector3(
                Mathf.Cos(theta) * r,
                y,
                Mathf.Sin(theta) * r
            );
        }

        return directions;
    }
}