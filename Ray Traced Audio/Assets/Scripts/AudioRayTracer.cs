using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Fires rays from the listener through the voxel grid on a background thread.
///
/// Ray contribution system:
///   - Rays continue bouncing until maxBounces regardless of LOS success
///   - Each bounce n contributes 2^(maxBounces - n) to successSum if LOS to source is clear
///   - Total possible per ray = 2^maxBounces - 1 (precomputed)
///   - Total possible overall = rayCount * (2^maxBounces - 1)
///   - Integer accumulation until final division — no rounding error
///   - Empty room = all rays succeed all bounces = muffle 0 guaranteed
///   - Sealed room = no ray ever succeeds = muffle 1 guaranteed
///
/// Two modes set by VoxelVolume at startup:
///   Static  (useDynamicObstacles = OFF): reads cached BitArray directly, zero overhead
///   Dynamic (useDynamicObstacles = ON):  snapshots combined BitArray from DynamismManager
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

    [Tooltip("How many times a ray bounces. Every bounce gets a chance to check LOS.")]
    public int maxBounces = 5;

    [Tooltip("Max voxel steps per ray segment.")]
    public int maxStepsPerSegment = 500;

    [Header("Audio Settings")]
    public AudioMixer audioMixer;

    // -------------------------------------------------------------------------
    // Mode

    private bool _isDynamic = false;
    private BitArray _staticBits;

    // -------------------------------------------------------------------------
    // Precomputed contribution values

    // _bounceContributions[n-1] = 2^(maxBounces - n) for bounce n (1-indexed)
    private long[] _bounceContributions;

    // Maximum possible successSum = rayCount * (2^maxBounces - 1)
    private long _totalPossible;

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

    public void InitializeStatic(VoxelGridData voxelData, BitArray staticBits)
    {
        _voxelData = voxelData;
        _staticBits = staticBits;
        _isDynamic = false;
        InitializeShared();
        Debug.Log("[AudioRaytracer] Initialized in STATIC mode.");
    }

    public void InitializeDynamic(VoxelGridData voxelData)
    {
        _voxelData = voxelData;
        _isDynamic = true;
        InitializeShared();
        Debug.Log("[AudioRaytracer] Initialized in DYNAMIC mode.");
    }

    private void InitializeShared()
    {
        _rayDirections = GenerateFibonacciSphere(rayCount);

        // Precompute bounce contributions — bounce n contributes 2^(maxBounces - n)
        // Bounce 1 (first hit) gets highest weight, bounce maxBounces gets 1
        _bounceContributions = new long[maxBounces];
        for (int n = 1; n <= maxBounces; n++)
            _bounceContributions[n - 1] = 1L << (maxBounces - n);

        // Total possible = rayCount * (2^maxBounces - 1)
        _totalPossible = (long)rayCount * ((1L << maxBounces) - 1);

        _workerThread = new Thread(RaytraceWorker);
        _workerThread.IsBackground = true;
        _workerThread.Start();
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
        float cutoff = Mathf.Lerp(500f, 22000f, 1f - muffle);
        audioMixer.SetFloat("MuffleFrequency", cutoff);

        Debug.Log($"[AudioRaytracer] Muffle: {muffle:F3} | Clarity: {(1f - muffle) * 100f:F1}%");
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

            BitArray bits;
            if (_isDynamic)
            {
                if (DynamismManager.Instance == null) continue;
                bits = DynamismManager.Instance.SnapshotCombinedBits();
            }
            else
            {
                bits = _staticBits;
            }

            if (bits == null || _voxelData == null) continue;

            _muffleRatio = CalculateMuffleRatio(sourcePos, listenerPos, bits);
        }
    }

    // -------------------------------------------------------------------------

    private float CalculateMuffleRatio(Vector3 sourcePos, Vector3 listenerPos, BitArray voxelBits)
    {
        long successSum = 0;

        for (int i = 0; i < _rayDirections.Length; i++)
        {
            Vector3 direction = _rayDirections[i];
            Vector3 currentOrigin = listenerPos;

            for (int bounce = 1; bounce <= maxBounces; bounce++)
            {
                DDATraversal.MarchResult march = DDATraversal.March(
                    currentOrigin,
                    direction,
                    _voxelData,
                    voxelBits,
                    maxStepsPerSegment);

                // Ray escaped the grid — no more surfaces to bounce off
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
                    successSum += _bounceContributions[bounce - 1];

                // Always reflect and continue — even after a successful LOS
                direction = Vector3.Reflect(direction, march.normal);
                currentOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.1f);
            }
        }

        return 1f - (float)successSum / _totalPossible;
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