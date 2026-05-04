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
///   - maxPossible is accumulated at runtime — only bounces that actually hit a surface
///     count toward the denominator. Rays that escape the grid without hitting anything
///     are excluded entirely, correctly handling open worlds and outdoor environments.
///   - If no ray hits any surface (fully open world), muffle = 0
///   - Integer accumulation until final division — no rounding error
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

    [Header("Reverb Settings")]
    [Tooltip("Min/Max decay time in seconds mapped to short/long travel distance.")]
    public float minDecayTime = 0.1f;
    public float maxDecayTime = 3.0f;

    [Tooltip("Min/Max reverb delay in seconds.")]
    public float minReverbDelay = 0.0f;
    public float maxReverbDelay = 0.1f;

    [Tooltip("Min/Max reflect delay in seconds.")]
    public float minReflectDelay = 0.0f;
    public float maxReflectDelay = 0.05f;

    public float delayMultiplier = 1f;

    [Tooltip("Energy lost per meter of solid material. Higher = thicker effective walls.")]
    public float absorptionCoefficient = 10f;

    // -------------------------------------------------------------------------
    // Mode

    private bool _isDynamic = false;
    private BitArray _staticBits;

    // -------------------------------------------------------------------------
    // Precomputed per-bounce contribution weights
    // _bounceContributions[n-1] = 2^(maxBounces - n) for bounce n (1-indexed)
    private long[] _bounceContributions;

    // -------------------------------------------------------------------------
    // Threading

    private Vector3 _sourcePos;
    private Vector3 _listenerPos;
    private readonly object _posLock = new object();

    private volatile float _muffleRatio = 0f;
    private volatile float _reverbRoom = -10000f;
    private volatile float _reverbDelay = 0f;
    private readonly SemaphoreSlim _workSignal = new SemaphoreSlim(0, 1);
    private volatile bool _shutdown = false;

    private Transform _source;
    private Transform _listener;
    private Vector3[] _rayDirections;
    private Thread _workerThread;

    private float _maxExpectedRoomSize;

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
        _bounceContributions = new long[maxBounces];
        for (int n = 1; n <= maxBounces; n++)
            _bounceContributions[n - 1] = 1L << (maxBounces - n);

        _maxExpectedRoomSize = new Vector3(
            _voxelData.gridSize.x * _voxelData.voxelSize,
            _voxelData.gridSize.y * _voxelData.voxelSize,
            _voxelData.gridSize.z * _voxelData.voxelSize).magnitude;

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

        audioMixer.SetFloat("ReverbRoom", _reverbRoom);
        audioMixer.SetFloat("ReverbDelay", _reverbDelay);

        string muffleStr = TopDownController.MuffleEnabled
            ? $"Muffle: {muffle:F3} | Clarity: {(1f - muffle) * 100f:F1}%"
            : "Muffle: Disabled";

        string echoStr = TopDownController.EchoEnabled
            ? $"Reverb Room: {_reverbRoom:F1} mB | Reverb Delay: {_reverbDelay:F3} s"
            : "Echo: Disabled";

        Debug.Log($"[AudioRaytracer] {muffleStr} | {echoStr}");


        if (!TopDownController.MuffleEnabled)
            audioMixer.SetFloat("MuffleFrequency", 22000f);

        if (!TopDownController.EchoEnabled)
        {
            audioMixer.SetFloat("ReverbRoom", -10000f);
            audioMixer.SetFloat("ReverbDelay", minReverbDelay);
        }
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

            var result = CalculateAudioProperties(sourcePos, listenerPos, bits);
            _muffleRatio = result.muffle;
            _reverbRoom = result.reverbRoom;
            _reverbDelay = result.reverbDelay;
        }
    }

    // -------------------------------------------------------------------------

    private struct AudioResult
    {
        public float muffle;
        public float reverbRoom;
        public float reverbDelay;
    }

    private AudioResult CalculateAudioProperties(Vector3 sourcePos, Vector3 listenerPos, BitArray voxelBits)
    {
        long successSum = 0;
        long maxPossible = 0;
        long echoSuccessCount = 0;
        float totalEchoDistance = 0f;

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

                // Ray escaped the grid without hitting anything —
                // this bounce and all remaining bounces are excluded from the denominator
                if (!march.hitSolid)
                    break;

                // This bounce hit a surface — it counts toward the denominator
                // regardless of whether LOS to the source succeeds
                long weight = _bounceContributions[bounce - 1];
                maxPossible += weight;

                Vector3 losOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.6f);

                if (TopDownController.MuffleEnabled)
                {
                    float permeation = DDATraversal.GetPermeationFactor(
                        losOrigin, sourcePos, _voxelData, voxelBits,
                        maxStepsPerSegment, absorptionCoefficient);
                    successSum += (long)(weight * permeation);
                }

                if (TopDownController.EchoEnabled)
                {
                    bool hasEchoLOS = DDATraversal.HasLineOfSight(
                        losOrigin, listenerPos, _voxelData, voxelBits, maxStepsPerSegment);
                    if (hasEchoLOS)
                    {
                        echoSuccessCount++;
                        totalEchoDistance += Vector3.Distance(currentOrigin, march.hitPoint);
                    }
                }

                direction = Vector3.Reflect(direction, march.normal);
                currentOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.1f);
            }
        }

        if (maxPossible == 0)
            return new AudioResult { muffle = 0f, reverbRoom = -10000f, reverbDelay = minReverbDelay };

        float muffle = 1f - (float)successSum / maxPossible;

        float echoRatio = (float)echoSuccessCount / (_rayDirections.Length * maxBounces);
        float avgDistance = echoSuccessCount > 0 ? totalEchoDistance / echoSuccessCount : 0f;

        float room = Mathf.Lerp(-10000f, 0f, echoRatio);
        float reverbDelay = Mathf.Min(delayMultiplier * avgDistance / 343f, 0.1f);

        return new AudioResult
        {
            muffle = muffle,
            reverbRoom = room,
            reverbDelay = reverbDelay,
        };
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