using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Sprint "impact of speed":
/// - light fisheye + radial blur: EyeFall/SprintFisheye fullscreen Shader Graph (Full Screen Pass
///   renderer feature), driven by the global _EyeFallSpeed. It runs after post-processing on the whole
///   camera image, so the first-person hands bend too;
/// - a post-process volume (chromatic aberration, stronger vignette) whose weight follows speed;
/// - speed-line particles (EyeFall/SpeedLine Shader Graph) and a subtle camera shake.
/// </summary>
public class SpeedEffects : MonoBehaviour
{
    public ParkourController player;
    [Tooltip("Material using EyeFall/SpeedLine.")]
    public Material speedLineMaterial;

    [Header("Speed range (m/s) mapped to 0..1")]
    public float minSpeed = 7f;
    public float maxSpeed = 11.5f;
    public float smoothing = 6f;

    [Header("Lens (fisheye strength lives on the SprintFisheye material)")]
    [Range(0f, 1f)] public float chromaticAberration = 0.45f;
    [Range(0f, 1f)] public float vignette = 0.45f;

    [Header("Speed lines")]
    public float linesPerSecond = 180f;
    public float lineRadius = 2.4f;
    public float lineDistance = 12f;
    [Tooltip("How much vertical velocity bends the line stream (jump up = lines fall, falling = lines rise).")]
    public float verticalInfluence = 1.6f;
    [Tooltip("How much sideways velocity (strafe, air strafe, wall run) bends the stream.")]
    public float lateralInfluence = 1f;
    [Tooltip("Max bend away from straight ahead, in degrees.")]
    public float maxBendAngle = 50f;
    public float bendSmoothing = 7f;

    [Header("Camera shake (degrees)")]
    public float shakeAmount = 0.35f;
    public float shakeFrequency = 16f;

    public float Intensity { get; private set; }

    Volume volume;
    VolumeProfile profile;
    ParticleSystem lines;
    ParticleSystem.EmissionModule linesEmission;
    Vector3 flowDir = Vector3.forward;

    void Start()
    {
        if (!player) player = GetComponent<ParkourController>();
        if (!player) player = FindAnyObjectByType<ParkourController>();
        if (!player || !player.cam) { enabled = false; return; }
        BuildVolume();
        BuildLines();
    }

    static readonly int SpeedId = Shader.PropertyToID("_EyeFallSpeed");

    void OnDisable() => Shader.SetGlobalFloat(SpeedId, 0f);

    void OnDestroy()
    {
        Shader.SetGlobalFloat(SpeedId, 0f);
        if (profile) Destroy(profile);
    }

    void BuildVolume()
    {
        profile = ScriptableObject.CreateInstance<VolumeProfile>();
        profile.Add<ChromaticAberration>().intensity.Override(chromaticAberration);
        var vig = profile.Add<Vignette>();
        vig.intensity.Override(vignette);
        vig.smoothness.Override(0.5f);

        var go = new GameObject("Speed Volume");
        go.transform.SetParent(transform, false);
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f;
        volume.weight = 0f;
        volume.sharedProfile = profile;
    }

    void BuildLines()
    {
        var go = new GameObject("Speed Lines");
        go.transform.SetParent(player.cam.transform, false);
        // Emit from a ring ahead of the camera, travelling back past it (local +Z of the system = towards camera).
        go.transform.localPosition = new Vector3(0f, 0f, lineDistance);
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        lines = go.AddComponent<ParticleSystem>();
        lines.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = lines.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(32f, 42f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.95f, 0.9f, 0.25f), new Color(1f, 0.85f, 0.75f, 0.6f));
        main.maxParticles = 400;

        linesEmission = lines.emission;
        linesEmission.rateOverTime = 0f;

        var shape = lines.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 0f;
        shape.radius = lineRadius;
        shape.radiusThickness = 0.15f;

        var col = lines.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Stretch;
        r.velocityScale = 0.06f;
        r.lengthScale = 2f;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sharedMaterial = speedLineMaterial ? speedLineMaterial : new Material(Shader.Find("EyeFall/SpeedLine"));

        lines.Play();
    }

    void Update()
    {
        float target = player.IsMantling ? Intensity : Mathf.InverseLerp(minSpeed, maxSpeed, player.HorizontalSpeed);
        if (player.IsFinished) target = 0f;
        Intensity = Mathf.Lerp(Intensity, target, 1f - Mathf.Exp(-smoothing * Time.deltaTime));

        Shader.SetGlobalFloat(SpeedId, Intensity);
        if (volume) volume.weight = Intensity;
        if (lines)
        {
            linesEmission.rateOverTime = linesPerSecond * Intensity * Intensity;
            BendLines();
        }
    }

    /// <summary>
    /// Points the emitter along the body's real velocity in camera space, so the world streams past
    /// in the opposite direction: lines sweep down on the way up, up while falling, sideways on strafes.
    /// </summary>
    void BendLines()
    {
        // Exaggerate only the world-vertical part (gravity), so looking up/down doesn't bend the stream.
        Vector3 world = player.Velocity;
        world.y *= verticalInfluence;
        Vector3 v = player.cam.transform.InverseTransformDirection(world);
        Vector3 target = Vector3.forward;
        float planar = new Vector2(v.x, v.z).magnitude;
        if (player.HorizontalSpeed > 0.5f)
        {
            target = new Vector3(v.x * lateralInfluence, v.y, Mathf.Max(v.z, planar * 0.35f));
            target = Vector3.RotateTowards(Vector3.forward, target.normalized, maxBendAngle * Mathf.Deg2Rad, 0f);
        }
        flowDir = Vector3.Slerp(flowDir, target, 1f - Mathf.Exp(-bendSmoothing * Time.deltaTime)).normalized;

        var t = lines.transform;
        t.localPosition = flowDir * lineDistance;
        t.localRotation = Quaternion.LookRotation(-flowDir, Vector3.up);
    }

    void LateUpdate()
    {
        // Runs after ParkourController has written the camera rotation this frame.
        if (!player.cameraRoot || Intensity < 0.01f) return;
        float t = Time.time * shakeFrequency;
        float a = shakeAmount * Intensity;
        var shake = Quaternion.Euler(
            (Mathf.PerlinNoise(t, 0.1f) - 0.5f) * 2f * a,
            (Mathf.PerlinNoise(0.3f, t) - 0.5f) * 2f * a,
            (Mathf.PerlinNoise(t, 0.7f) - 0.5f) * 2f * a * 0.6f);
        player.cameraRoot.localRotation *= shake;
    }
}
