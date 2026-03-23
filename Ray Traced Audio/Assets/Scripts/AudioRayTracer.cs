using System.Collections;
using System.Threading;
using UnityEngine;

/// <summary>
/// Fires rays from the listener through the baked voxel grid on a background thread.
///
/// Threading model:
/// - Main thread copies source/listener positions each frame and signals new work
/// - Background thread runs continuously, picks up work when signaled
/// - Result is written to a volatile float — main thread reads and logs it
/// - VoxelGridData is read-only, no lock needed
/// - BitArray is copied once at startup and never written — no lock needed
///
/// Muffle Ratio: 0.0 = fully clear, 1.0 = fully blocked.
/// </summary>
public class AudioRaytracer : MonoBehaviour
{
    private VoxelGridData voxelData;

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

    // -------------------------------------------------------------------------
    // Shared state between main thread and background thread

    private Vector3 _sourcePos;
    private Vector3 _listenerPos;
    private readonly object _posLock = new object();

    // Latest muffle result — volatile ensures main thread always reads the freshest value
    private volatile float _muffleRatio = 1f;

    // Signals background thread that new positions are ready
    private readonly SemaphoreSlim _workSignal = new SemaphoreSlim(0, 1);

    private volatile bool _shutdown = false;

    // -------------------------------------------------------------------------

    private Transform _source;
    private Transform _listener;
    private Vector3[] _rayDirections;
    private BitArray _voxelBits;
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

    public void Initialize(VoxelGridData voxelData)
    {
        this.voxelData = voxelData;
        _voxelBits = voxelData.GetBitArray();

        _rayDirections = GenerateFibonacciSphere(rayCount);

        _workerThread = new Thread(RaytraceWorker);
        _workerThread.IsBackground = true;
        _workerThread.Start();
    }

    private void Update()
    {
        if (_source == null || _listener == null) return;

        // Copy latest positions for the background thread
        lock (_posLock)
        {
            _sourcePos = _source.position;
            _listenerPos = _listener.position;
        }

        // Signal work — only if the worker isn't already busy
        if (_workSignal.CurrentCount == 0)
            _workSignal.Release();

        // Read and display latest result
        float muffle = _muffleRatio;
        int successCount = Mathf.RoundToInt((1f - muffle) * rayCount);

        Debug.Log($"[AudioRaytracer] Muffle: {muffle:F3} | " +
                  $"Rays succeeded: {successCount}/{rayCount} | " +
                  $"Clarity: {(1f - muffle) * 100f:F1}%");
    }

    private void OnDestroy()
    {
        _shutdown = true;
        try { _workSignal.Release(); } catch { } // unblock worker so it can exit cleanly
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

            _muffleRatio = CalculateMuffleRatio(sourcePos, listenerPos);
        }
    }

    // -------------------------------------------------------------------------

    private float CalculateMuffleRatio(Vector3 sourcePos, Vector3 listenerPos)
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
                    voxelData,
                    _voxelBits,
                    maxStepsPerSegment);

                if (!march.hitSolid)
                    break;

                Vector3 losOrigin = march.hitPoint + march.normal * (voxelData.voxelSize * 0.6f);

                bool hasLOS = DDATraversal.HasLineOfSight(
                    losOrigin,
                    sourcePos,
                    voxelData,
                    _voxelBits,
                    maxStepsPerSegment);

                if (hasLOS)
                {
                    successCount++;
                    break;
                }

                if (bounce < maxBounces)
                {
                    direction = Vector3.Reflect(direction, march.normal);
                    currentOrigin = march.hitPoint + march.normal * (voxelData.voxelSize * 0.1f);
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