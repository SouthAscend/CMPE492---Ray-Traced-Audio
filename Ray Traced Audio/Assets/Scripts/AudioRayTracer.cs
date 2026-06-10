using System.Collections;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Fires rays from the listener through the voxel grid on a background thread.
///
/// Ray contribution system:
///   - Rays march until they hit a solid surface, come within sourceProximityRadius
///     of the source, or escape the grid.
///   - Proximity hit: all remaining bounce weights credited as full success, ray stops.
///   - Solid hit: permeation factor applied to determine partial success.
///   - maxPossible accumulated at runtime — open world correctly returns 0 muffle.
///   - Two separate weight systems:
///       Muffle: 2^(maxBounces - bounce) — halved per bounce
///       Direction: 0.2^(bounce - 1) — fifthd per bounce, heavily prioritizes first bounce
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

    [Tooltip("How many times a ray bounces.")]
    public int maxBounces = 5;

    [Tooltip("Max voxel steps per ray segment.")]
    public int maxStepsPerSegment = 500;

    [Tooltip("Radius in meters around the source. Any ray entering this radius counts as a direct hit.")]
    public float sourceProximityRadius = 0.1f;

    [Header("Audio Settings")]
    public AudioMixer audioMixer;

    [Header("Reverb Settings")]
    public float minReverbDelay = 0.0f;
    public float delayMultiplier = 1f;

    [Tooltip("Energy lost per meter of solid material. Higher = more aggressive wall absorption.")]
    public float absorptionCoefficient = 10f;

    [Tooltip("Power applied to the raw muffle value. Higher = more forgiving at low muffle values.")]
    public float muffleCurveCoefficient = 2.5f;

    [Header("Directional Settings")]
    [Tooltip("Direction weight multiplier per bounce. 0.2 = fifth per bounce, 0.1 = tenth per bounce. Lower = first bounce dominates more.")]
    public float directionBounceDecay = 0.2f;

    [Tooltip("Direct path bias as a ratio of first-reflection pool weight. 0.1 = 10% of first reflections.")]
    public float directBiasRatio = 0.1f;

    [Tooltip("Absorption coefficient for directional bias. Lower than muffle absorption — bias survives thicker walls.")]
    public float directAbsorptionCoefficient = 2f;

    [Tooltip("Power for directional bias falloff curve. 1 = standard exponential, 2 = Gaussian (forgiving at start).")]
    public float directAbsorptionPower = 2f;

    // -------------------------------------------------------------------------
    // Mode

    private bool _isDynamic = false;
    private BitArray _staticBits;

    // -------------------------------------------------------------------------
    // Precomputed muffle bounce weights: _bounceContributions[n-1] = 2^(maxBounces - n)
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

    private volatile float _perceivedDirX = 0f;
    private volatile float _perceivedDirY = 0f;
    private volatile float _perceivedDirZ = 0f;

    private readonly object _successfulDirsLock = new object();
    private Vector4[] _successfulRayDirs = new Vector4[0];

    [HideInInspector]
    public bool _use2DMode = false;

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
        _rayDirections = _use2DMode
            ? GenerateCircle(rayCount)
            : GenerateFibonacciSphere(rayCount);

        _bounceContributions = new long[maxBounces];
        for (int n = 1; n <= maxBounces; n++)
            _bounceContributions[n - 1] = 1L << (maxBounces - n);

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

        if (!TopDownController.MuffleEnabled)
            cutoff = 22000f;

        audioMixer.SetFloat("MuffleFrequency", cutoff);

        if (!TopDownController.EchoEnabled)
        {
            audioMixer.SetFloat("ReverbRoom", -10000f);
            audioMixer.SetFloat("ReverbDelay", minReverbDelay);
        }
        else
        {
            audioMixer.SetFloat("ReverbRoom", _reverbRoom);
            audioMixer.SetFloat("ReverbDelay", _reverbDelay);
        }

        float solidDistance = DDATraversal.GetSolidDistance(
            _listenerPos + ((_sourcePos - _listenerPos).normalized) * (_voxelData.voxelSize * 0.6f),
            _sourcePos, _voxelData, _staticBits ?? DynamismManager.Instance?.SnapshotCombinedBits(),
            maxStepsPerSegment);
        float clearRatio = Mathf.Exp(-solidDistance * directAbsorptionCoefficient);

        string muffleStr = TopDownController.MuffleEnabled
            ? $"Muffle: {muffle:F3} | Clarity: {(1f - muffle) * 100f:F1}% | Direct Clear: {clearRatio * 100f:F1}% | Permeation: {(TopDownController.PermeationEnabled ? "On" : "Off")}"
            : "Muffle: Disabled";

        string echoStr = TopDownController.EchoEnabled
            ? $"Reverb Room: {_reverbRoom:F1} mB | Reverb Delay: {_reverbDelay:F3} s"
            : "Echo: Disabled";

#if UNITY_EDITOR
        Debug.Log($"[AudioRaytracer] {muffleStr} | {echoStr}");
#endif
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
            _perceivedDirX = result.perceivedDirection.x;
            _perceivedDirY = result.perceivedDirection.y;
            _perceivedDirZ = result.perceivedDirection.z;
            lock (_successfulDirsLock)
                _successfulRayDirs = result.successfulRayDirs ?? new Vector4[0];
        }
    }

    // -------------------------------------------------------------------------

    private struct AudioResult
    {
        public float muffle;
        public float reverbRoom;
        public float reverbDelay;
        public Vector3 perceivedDirection;
        public Vector4[] successfulRayDirs;
    }

    private AudioResult CalculateAudioProperties(Vector3 sourcePos, Vector3 listenerPos, BitArray voxelBits)
    {
        long successSum = 0;
        long maxPossible = 0;
        long echoSuccessCount = 0;
        float totalEchoDistance = 0f;
        Vector3 weightedDirSum = Vector3.zero;
        float totalDirWeight = 0f;
        float effectiveProximityRadius = Vector3.Distance(sourcePos, listenerPos) <= sourceProximityRadius
            ? 0f
            : sourceProximityRadius;

        Vector3 directDir = (sourcePos - listenerPos).normalized;
        var successfulDirs = new System.Collections.Generic.List<Vector4>();

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
                    maxStepsPerSegment,
                    sourcePos,
                    effectiveProximityRadius);

                // Ray escaped — no more bounces possible
                if (march.escaped)
                    break;

                // Proximity hit — ray reached the source zone
                // Credit all remaining bounces as full success and stop
                if (march.hitProximity)
                {
                    for (int remaining = bounce; remaining <= maxBounces; remaining++)
                    {
                        long w = _bounceContributions[remaining - 1];
                        maxPossible += w;
                        successSum += w;
                    }

                    // Direction: heavily weight first bounce, fifth per bounce after
                    float dirWeight = Mathf.Pow(directionBounceDecay, bounce - 1);
                    weightedDirSum += _rayDirections[i] * dirWeight;
                    totalDirWeight += dirWeight;

                    if (bounce == 1) successfulDirs.Add(new Vector4(_rayDirections[i].x, _rayDirections[i].y, _rayDirections[i].z, 1.0f));

                    break;
                }

                // Solid hit — apply permeation or LOS check
                long muffleWeight = _bounceContributions[bounce - 1];
                maxPossible += muffleWeight;

                Vector3 losOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.6f);

                if (TopDownController.MuffleEnabled)
                {
                    float permeation = TopDownController.PermeationEnabled
                        ? DDATraversal.GetPermeationFactor(
                            losOrigin, sourcePos, _voxelData, voxelBits,
                            maxStepsPerSegment, absorptionCoefficient)
                        : (DDATraversal.HasLineOfSight(
                            losOrigin, sourcePos, _voxelData, voxelBits,
                            maxStepsPerSegment) ? 1f : 0f);

                    successSum += (long)(muffleWeight * permeation);

                    // Direction weight: separate from muffle, fifthd per bounce
                    float dirWeight = permeation * Mathf.Pow(directionBounceDecay, bounce);
                    weightedDirSum += _rayDirections[i] * dirWeight;
                    totalDirWeight += dirWeight;

                    if (permeation > 0.5f && bounce == 1) successfulDirs.Add(new Vector4(_rayDirections[i].x, _rayDirections[i].y, _rayDirections[i].z, directionBounceDecay));
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

                direction = _use2DMode
                    ? new Vector3(direction.x, 0f, direction.z).normalized
                    : Vector3.Reflect(direction, march.normal);
                currentOrigin = march.hitPoint + march.normal * (_voxelData.voxelSize * 0.1f);
            }
        }

        if (TopDownController.MuffleEnabled)
        {
            Vector3 directLosOrigin = listenerPos + directDir * (_voxelData.voxelSize * 0.6f);
            float solidDistance = DDATraversal.GetSolidDistance(directLosOrigin, sourcePos, _voxelData, voxelBits, maxStepsPerSegment);
            float directPermeation = Mathf.Exp(-Mathf.Pow(solidDistance * directAbsorptionCoefficient, directAbsorptionPower));

            if (directPermeation > 0f)
            {
                float firstReflectionPool = rayCount * directionBounceDecay;
                float biasWeight = directBiasRatio * firstReflectionPool * directPermeation;
                weightedDirSum += directDir * biasWeight;
                totalDirWeight += biasWeight;
            }
        }

        if (maxPossible == 0)
            return new AudioResult { muffle = 0f, reverbRoom = -10000f, reverbDelay = minReverbDelay };

        float muffle = 1f - (float)successSum / maxPossible;
        muffle = Mathf.Pow(muffle, muffleCurveCoefficient);

        float echoRatio = (float)echoSuccessCount / (_rayDirections.Length * maxBounces);
        float avgDistance = echoSuccessCount > 0 ? totalEchoDistance / echoSuccessCount : 0f;

        float room = Mathf.Lerp(-10000f, 0f, echoRatio);
        float reverbDelay = Mathf.Min(delayMultiplier * avgDistance / 343f, 0.1f);

        Vector3 perceivedRaw = totalDirWeight > 0f
            ? weightedDirSum / totalDirWeight
            : directDir;

        if (_use2DMode)
            perceivedRaw.y = 0f;

        Vector3 perceived = perceivedRaw.normalized;

        return new AudioResult
        {
            muffle = muffle,
            reverbRoom = room,
            reverbDelay = reverbDelay,
            perceivedDirection = perceived,
            successfulRayDirs = successfulDirs.ToArray()
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

    private Vector3[] GenerateCircle(int n)
    {
        Vector3[] directions = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float angle = (float)i / n * Mathf.PI * 2f;
            directions[i] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }
        return directions;
    }

    private void OnDrawGizmos()
    {
        if (_listener == null) return;

        Vector4[] dirs;
        lock (_successfulDirsLock)
            dirs = _successfulRayDirs;

        Gizmos.color = Color.cyan;
        foreach (Vector4 d in dirs)
        {
            Vector3 dir = new Vector3(d.x, d.y, d.z);
            float length = 3f * d.w;
            Gizmos.DrawLine(_listener.position, _listener.position + dir * length);
        }

        Vector3 perceivedDir = new Vector3(_perceivedDirX, _perceivedDirY, _perceivedDirZ);
        if (perceivedDir != Vector3.zero)
        {
            Gizmos.color = Color.yellow;
            Vector3 start = _listener.position;
            Vector3 end = start + perceivedDir * 3f;

            Vector3 perp1 = Vector3.Cross(perceivedDir, Vector3.up).normalized;
            if (perp1.magnitude < 0.01f)
                perp1 = Vector3.Cross(perceivedDir, Vector3.right).normalized;
            Vector3 perp2 = Vector3.Cross(perceivedDir, perp1).normalized;

            float thickness = 0.05f;
            Gizmos.DrawLine(start, end);
            Gizmos.DrawLine(start + perp1 * thickness, end + perp1 * thickness);
            Gizmos.DrawLine(start - perp1 * thickness, end - perp1 * thickness);
            Gizmos.DrawLine(start + perp2 * thickness, end + perp2 * thickness);
            Gizmos.DrawLine(start - perp2 * thickness, end - perp2 * thickness);
        }
    }
}