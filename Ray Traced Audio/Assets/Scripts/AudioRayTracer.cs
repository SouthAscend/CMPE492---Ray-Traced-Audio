using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Fires rays from the listener through the voxel grid on a background thread.
///
/// Two modes set by VoxelVolume at startup:
///
///   Static mode  (useDynamicObstacles = OFF):
///     Reads a single BitArray cached at startup. No locks, no copies, no overhead.
///     Fastest possible — use for fully static scenes.
///
///   Dynamic mode (useDynamicObstacles = ON):
///     Snapshots the combined BitArray from DynamismManager each raytrace cycle.
///     Supports runtime obstacle changes at a small per-cycle cost.
///
/// Muffle Ratio: 0.0 = fully clear, 1.0 = fully blocked.
/// </summary>
public class AudioRaytracer : MonoBehaviour
{
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
    // Mode

    private bool _isDynamic = false;

    // Static mode — cached once, never changes, no lock needed
    private BitArray _staticBits;

    // -------------------------------------------------------------------------
    // Threading

    private Vector3 _sourcePos;
    private Vector3 _listenerPos;
    private readonly object _posLock = new object();

    private volatile float _muffleRatio = 1f;
    private readonly SemaphoreSlim _workSignal = new SemaphoreSlim(0, 1);
    private volatile bool _shutdown = false;

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
    /// Static mode initialization — called by VoxelVolume when useDynamicObstacles is OFF.
    /// BitArray is cached directly, no DynamismManager involved.
    /// </summary>
    public void InitializeStatic(VoxelGridData voxelData, BitArray staticBits)
    {
        _voxelData = voxelData;
        _staticBits = staticBits;
        _isDynamic = false;

        _rayDirections = GenerateFibonacciSphere(rayCount);

        _workerThread = new Thread(RaytraceWorker);
        _workerThread.IsBackground = true;
        _workerThread.Start();

        Debug.Log("[AudioRaytracer] Initialized in STATIC mode.");
    }

    /// <summary>
    /// Dynamic mode initialization — called by VoxelVolume when useDynamicObstacles is ON.
    /// Bits are snapshotted from DynamismManager each raytrace cycle.
    /// </summary>
    public void InitializeDynamic(VoxelGridData voxelData)
    {
        _voxelData = voxelData;
        _isDynamic = true;

        _rayDirections = GenerateFibonacciSphere(rayCount);

        _workerThread = new Thread(RaytraceWorker);
        _workerThread.IsBackground = true;
        _workerThread.Start();

        Debug.Log("[AudioRaytracer] Initialized in DYNAMIC mode.");
    }

    // -------------------------------------------------------------------------

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

            // Resolve which bits to use based on mode
            BitArray bits;
            if (_isDynamic)
            {
                if (DynamismManager.Instance == null) continue;
                bits = DynamismManager.Instance.SnapshotCombinedBits();
            }
            else
            {
                // Static — use cached bits directly, no copy needed
                bits = _staticBits;
            }

            if (bits == null || _voxelData == null) continue;

            _muffleRatio = CalculateMuffleRatio(sourcePos, listenerPos, bits);
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