using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 2D rotoscoped first-person hands, drawn on a quad just in front of the camera with the
/// EyeFall/HandsLit Shader Graph so scene lights and shadows affect them.
/// Frames come from a grid sprite sheet, numbered left-to-right, top-to-bottom starting at 0.
/// Run A plays while running, Run B while sprinting / wall running, Jump once per jump or mantle,
/// Idle holds a lowered pose that breathes. Slide lowers the hands out of view.
/// Every frame change drives a smear (EyeFall/HandsSmear Shader Graph, on a transparent quad right
/// behind the hands): a soft, streaked, tapered trail dragged from the new pose back toward the old one.
/// Hands sway behind mouse look / strafe with smoothing (same approach as the Mocap FPS gloves).
/// Breathing is asymmetric (short inhale, long exhale) and gets faster/deeper after sprinting.
/// </summary>
public class FirstPersonHands : MonoBehaviour
{
    [Serializable]
    public class HandAnim
    {
        public int firstFrame;
        public int frameCount = 1;
        public float fps = 12f;
        public bool loop = true;

        public HandAnim(int first, int count, float fps, bool loop)
        {
            firstFrame = first;
            frameCount = count;
            this.fps = fps;
            this.loop = loop;
        }
    }

    public ParkourController player;
    public Texture2D sheet;
    [Tooltip("Material using EyeFall/HandsLit. A copy is made at runtime.")]
    public Material handsMaterial;
    public int columns = 5;
    public int rows = 5;

    [Header("Animations (frame indices in the sheet)")]
    public HandAnim runA = new HandAnim(0, 8, 18f, true);
    public HandAnim runB = new HandAnim(8, 8, 22f, true);
    public HandAnim jump = new HandAnim(16, 6, 20f, false);
    [Tooltip("Pose held when standing still (it breathes). Set frameCount > 1 for an idle loop.")]
    public HandAnim idle = new HandAnim(2, 1, 1f, true);

    [Header("Behaviour")]
    [Tooltip("Horizontal speed above which Run B is used instead of Run A.")]
    public float sprintSpeedThreshold = 8f;
    [Tooltip("Run frame rate is multiplied by speed / this value (clamped).")]
    public float referenceSpeed = 8f;
    [Tooltip("Air time before a plain fall (no jump) also plays the jump animation.")]
    public float fallDelay = 0.2f;
    [Tooltip("How fast the hands drop out of / come back into view.")]
    public float showSpeed = 8f;
    [Tooltip("At idle the hands sit this fraction of the screen lower.")]
    [Range(0f, 0.6f)] public float idleDrop = 0.1f;

    [Header("Smear between frames")]
    [Tooltip("Material using EyeFall/HandsSmear. A copy is made at runtime.")]
    public Material smearMaterial;
    [Range(0f, 2f)] public float smearStrength = 1f;
    [Tooltip("How many frame durations the trail takes to retract and fade (> 1 keeps it flowing between frames).")]
    [Range(0.2f, 3f)] public float smearLength = 1.6f;
    [Tooltip("Max smear length in frame UV (0..1).")]
    public float maxSmear = 0.35f;

    [Header("Sway (hands lag behind the camera)")]
    [Tooltip("Screen-height fraction per pixel of mouse movement.")]
    public float swayAmount = 0.0025f;
    public float swayMax = 0.06f;
    public float swaySmooth = 10f;
    [Tooltip("Sideways lag when strafing (screen-height fraction).")]
    public float strafeSway = 0.02f;
    [Tooltip("Roll in degrees per unit of horizontal sway.")]
    public float swayRoll = 60f;

    [Header("Breathing")]
    public float calmBreathsPerMinute = 14f;
    public float windedBreathsPerMinute = 40f;
    [Tooltip("Hands vertical travel per breath, as a fraction of screen height.")]
    public float calmDepth = 0.012f;
    public float windedDepth = 0.035f;
    [Tooltip("Camera pitch per breath in degrees (at calm depth; scales with depth).")]
    public float cameraBreathPitch = 0.3f;
    [Tooltip("How fast sprinting makes you winded, and how fast you recover (per second).")]
    public float exertionGain = 0.2f;
    public float exertionRecovery = 0.06f;
    [Tooltip("Breathing amount while running (idle = 1).")]
    [Range(0f, 1f)] public float breathWhileMoving = 0.4f;

    [Header("Look")]
    public Color tint = Color.white;
    [Range(0f, 1f)] public float lightInfluence = 1f;
    [Tooltip("Distance of the hands quad from the camera. Keep it inside the player capsule (< radius).")]
    public float quadDistance = 0.1f;
    [Range(1f, 1.3f)] public float overscan = 1.08f;

    public float Exertion { get; private set; }

    Camera cam;
    Transform quad;
    Material mat;
    HandAnim current;
    float frameTime;
    float shown;
    float airTime;
    bool jumpPlaying;
    float frameAspect = 16f / 9f;
    float playRate = 1f;

    Vector2[] centroids;
    int shownFrame = -1;
    float smear;
    Vector2 smearDir;
    Material smearMat;
    Vector2 sway;

    float breathPhase;
    float breath;           // 0 = exhaled, 1 = inhaled
    float breathAmount;     // 1 at idle, breathWhileMoving while moving
    float idleAmount;       // 0..1 blend into the lowered idle pose

    static readonly int FrameScale = Shader.PropertyToID("_FrameScale");
    static readonly int FrameOffset = Shader.PropertyToID("_FrameOffset");
    static readonly int SmearDir = Shader.PropertyToID("_SmearDir");
    static readonly int Smear = Shader.PropertyToID("_Smear");
    static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int LightInfluence = Shader.PropertyToID("_LightInfluence");

    void Start()
    {
        if (!player) player = FindAnyObjectByType<ParkourController>();
        if (!player) return;
        player.Jumped += StartJump;
        cam = player.cam ? player.cam : Camera.main;
        ComputeCentroids();
        BuildQuad();
        breathPhase = UnityEngine.Random.value;
    }

    void OnDestroy()
    {
        if (player) player.Jumped -= StartJump;
        if (mat) Destroy(mat);
        if (smearMat) Destroy(smearMat);
    }

    /// <summary>Alpha-weighted centre of each cell (frame-local UV). Drives the smear direction.</summary>
    void ComputeCentroids()
    {
        int count = columns * rows;
        centroids = new Vector2[count];
        for (int i = 0; i < count; i++) centroids[i] = new Vector2(0.5f, 0.3f);
        if (!sheet || !sheet.isReadable) return;

        var px = sheet.GetPixels32();
        int w = sheet.width, cw = sheet.width / columns, ch = sheet.height / rows;
        for (int i = 0; i < count; i++)
        {
            int col = i % columns, row = i / columns;
            int x0 = col * cw, y0 = (rows - 1 - row) * ch;
            double sx = 0, sy = 0, sw = 0;
            for (int y = 0; y < ch; y += 2)
            for (int x = 0; x < cw; x += 2)
            {
                byte a = px[(y0 + y) * w + x0 + x].a;
                if (a < 128) continue;
                sx += x; sy += y; sw += 1;
            }
            if (sw > 0) centroids[i] = new Vector2((float)(sx / sw) / cw, (float)(sy / sw) / ch);
        }
    }

    void BuildQuad()
    {
        var go = new GameObject("Hands Quad", typeof(MeshFilter), typeof(MeshRenderer));
        quad = go.transform;
        quad.SetParent(cam.transform, false);

        var mesh = new Mesh { name = "HandsQuad" };
        mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0) };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;

        mat = handsMaterial ? new Material(handsMaterial) : new Material(Shader.Find("EyeFall/HandsLit"));
        if (sheet)
        {
            mat.SetTexture(BaseMap, sheet);
            frameAspect = (sheet.width / (float)columns) / (sheet.height / (float)rows);
        }
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = true;

        // Smear trail: same quad, a hair behind the hands so the hands always draw on top of it.
        var smearShader = Shader.Find("EyeFall/HandsSmear");
        if (smearMaterial || smearShader)
        {
            var sGo = new GameObject("Hands Smear", typeof(MeshFilter), typeof(MeshRenderer));
            sGo.transform.SetParent(quad, false);
            sGo.transform.localPosition = new Vector3(0f, 0f, 0.0005f);
            sGo.GetComponent<MeshFilter>().sharedMesh = mesh;
            smearMat = smearMaterial ? new Material(smearMaterial) : new Material(smearShader);
            if (sheet) smearMat.SetTexture(BaseMap, sheet);
            smearMat.SetFloat(Smear, 0f);
            var sr = sGo.GetComponent<MeshRenderer>();
            sr.sharedMaterial = smearMat;
            sr.shadowCastingMode = ShadowCastingMode.Off;
            sr.receiveShadows = false;
        }
    }

    void StartJump()
    {
        Play(jump, true);
        jumpPlaying = true;
    }

    void Play(HandAnim anim, bool restart)
    {
        if (anim == current && !restart) return;
        current = anim;
        frameTime = 0f;
    }

    void Update()
    {
        if (!mat || !player) return;
        float dt = Time.deltaTime;

        bool grounded = player.IsGrounded;
        airTime = grounded || player.IsWallRunning ? 0f : airTime + dt;
        if (!jumpPlaying && airTime > fallDelay && !player.IsMantling) StartJump();

        float speed = player.HorizontalSpeed;
        bool visible = true;
        bool idling = false;

        if (jumpPlaying)
        {
            // Hold the last jump frame until we are back on something.
            if ((grounded || player.IsWallRunning) && !player.IsMantling && frameTime * jump.fps >= jump.frameCount - 1)
                jumpPlaying = false;
        }

        if (!jumpPlaying)
        {
            if (player.IsWallRunning || (grounded && !player.IsSliding && speed > sprintSpeedThreshold))
                Play(runB, false);
            else if (grounded && !player.IsSliding && speed > 1f)
                Play(runA, false);
            else if (grounded && !player.IsSliding)
            {
                Play(idle, false);
                idling = true;
            }
            else
                visible = false;
        }

        if (player.IsFinished) visible = false;

        playRate = current == jump || current == idle ? 1f : Mathf.Clamp(speed / Mathf.Max(0.1f, referenceSpeed), 0.7f, 1.6f);
        if (visible) frameTime += dt * playRate;
        shown = Mathf.MoveTowards(shown, visible ? 1f : 0f, dt * showSpeed);
        idleAmount = Mathf.MoveTowards(idleAmount, idling ? 1f : 0f, dt * 3f);

        UpdateBreathing(dt, speed);

        mat.SetColor(BaseColor, tint);
        mat.SetFloat(LightInfluence, lightInfluence);
        if (current != null) ShowFrame(current.firstFrame + FrameInAnim(current));

        // Smear fades out over most of one frame's duration.
        float frameDuration = current != null ? 1f / Mathf.Max(1f, current.fps * playRate) : 0.1f;
        smear = Mathf.MoveTowards(smear, 0f, dt / (frameDuration * smearLength));
        if (smearMat) smearMat.SetFloat(Smear, smear * smear * (3f - 2f * smear)); // eased fade

        // Sway: hands lag behind the look direction and strafing (Mocap FPS gloves approach).
        Vector2 look = UnityEngine.InputSystem.Mouse.current != null && Cursor.lockState == CursorLockMode.Locked
            ? UnityEngine.InputSystem.Mouse.current.delta.ReadValue()
            : Vector2.zero;
        var targetSway = Vector2.ClampMagnitude(-look * swayAmount + new Vector2(-player.MoveInput.x * strafeSway, 0f), swayMax);
        sway = Vector2.Lerp(sway, targetSway, 1f - Mathf.Exp(-swaySmooth * dt));
    }

    void UpdateBreathing(float dt, float speed)
    {
        bool exerting = player.IsSprinting && speed > sprintSpeedThreshold || player.IsWallRunning || player.IsMantling;
        Exertion = Mathf.Clamp01(Exertion + (exerting ? exertionGain : -exertionRecovery) * dt);

        // Slight irregularity so it never feels like a metronome.
        float bpm = Mathf.Lerp(calmBreathsPerMinute, windedBreathsPerMinute, Exertion);
        bpm *= 1f + (Mathf.PerlinNoise(Time.time * 0.15f, 3.7f) - 0.5f) * 0.25f;
        breathPhase = (breathPhase + dt * bpm / 60f) % 1f;

        // Short inhale (40%), longer relaxed exhale (60%).
        const float inhale = 0.4f;
        breath = breathPhase < inhale
            ? Mathf.SmoothStep(0f, 1f, breathPhase / inhale)
            : 1f - Mathf.SmoothStep(0f, 1f, (breathPhase - inhale) / (1f - inhale));

        breathAmount = Mathf.Lerp(breathWhileMoving, 1f, idleAmount);
    }

    void LateUpdate()
    {
        if (!quad || !cam) return;
        // Size the quad so the 16:9 frame covers the whole view at quadDistance (FOV changes with speed).
        float h = 2f * quadDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float w = h * cam.aspect;
        if (w / h > frameAspect) h = w / frameAspect;
        else w = h * frameAspect;
        w *= overscan;   // a bit bigger than the screen so sway never reveals the cut arm edges
        h *= overscan;

        float depth = Mathf.Lerp(calmDepth, windedDepth, Exertion) * breathAmount;
        float b = (breath - 0.5f) * 2f;                       // -1 exhaled .. 1 inhaled
        float ease = 1f - Mathf.SmoothStep(0f, 1f, shown);
        float y = -h * ease - h * idleDrop * idleAmount + b * depth * h;
        float scale = 1f + breath * depth * 0.6f;              // chest rises, hands come a touch closer

        quad.localPosition = new Vector3(sway.x * h, y + sway.y * h, quadDistance);
        quad.localRotation = Quaternion.Euler(0f, 0f, b * depth * 20f + sway.x * swayRoll);
        quad.localScale = new Vector3(w * scale, h * scale, 1f);
        quad.gameObject.SetActive(shown > 0.001f);

        // The head follows the chest a little (runs after ParkourController wrote the rotation).
        if (player.cameraRoot)
        {
            float pitch = -b * cameraBreathPitch * (depth / Mathf.Max(0.0001f, calmDepth));
            player.cameraRoot.localRotation *= Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    int FrameInAnim(HandAnim a)
    {
        int n = Mathf.Max(1, a.frameCount);
        int f = Mathf.FloorToInt(frameTime * a.fps);
        return a.loop ? f % n : Mathf.Min(f, n - 1);
    }

    void ShowFrame(int index)
    {
        if (index != shownFrame)
        {
            if (shownFrame >= 0 && shown > 0.01f)
            {
                Vector2 d = centroids != null && index < centroids.Length && shownFrame < centroids.Length
                    ? centroids[index] - centroids[shownFrame]
                    : Vector2.zero;
                smearDir = Vector2.ClampMagnitude(d * smearStrength, maxSmear);
                smear = smearStrength > 0f ? 1f : 0f;
                if (smearMat) smearMat.SetVector(SmearDir, smearDir);
            }
            shownFrame = index;
        }

        float cw = 1f / columns, ch = 1f / rows;
        var scale = new Vector4(cw, ch, 0f, 0f);
        var offset = CellOffset(index);
        mat.SetVector(FrameScale, scale);
        mat.SetVector(FrameOffset, offset);
        if (smearMat)
        {
            smearMat.SetVector(FrameScale, scale);
            smearMat.SetVector(FrameOffset, offset);
        }
    }

    Vector4 CellOffset(int index)
    {
        int col = index % columns;
        int row = index / columns;
        float cw = 1f / columns, ch = 1f / rows;
        return new Vector4(col * cw, 1f - (row + 1) * ch, 0f, 0f);
    }
}
