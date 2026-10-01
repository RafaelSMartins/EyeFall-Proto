using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person parkour controller: walk / sprint, buffered + coyote jump, crouch & slide,
/// wall run with wall jump, and ledge mantle. Uses Input System actions (keyboard/mouse + gamepad).
/// Controls: WASD / arrows move, sprint is automatic when running forward (Shift forces it),
/// Space jump / mantle, Ctrl or C or Mouse4 crouch / slide, R restart, Esc release mouse.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class ParkourController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraRoot;
    public Camera cam;

    [Header("Movement")]
    public float walkSpeed = 6f;
    public float sprintSpeed = 10f;
    public float crouchSpeed = 3.5f;
    public enum SprintMode { Hold, Toggle, Auto }
    [Tooltip("Hold: hold Shift. Toggle: tap Shift. Auto: sprint after running forward for autoSprintDelay (Shift still works).")]
    public SprintMode sprintMode = SprintMode.Auto;
    public float autoSprintDelay = 0.5f;
    public float groundAccel = 70f;
    public float airAccel = 35f;
    [Tooltip("How fast (deg/s) air momentum turns toward the move input, keeping its speed.")]
    public float airTurnSpeed = 300f;
    public float gravity = -26f;
    public float jumpHeight = 1.4f;
    public float coyoteTime = 0.15f;
    public float jumpBuffer = 0.15f;

    [Header("Crouch / Slide")]
    public float standHeight = 1.8f;
    public float crouchHeight = 0.9f;
    public float slideBoost = 3.5f;
    public float slideFriction = 6f;
    public float slideMinSpeed = 4f;
    [Tooltip("Horizontal speed above which crouch starts a slide (any move input also counts).")]
    public float slideMoveThreshold = 0.5f;
    [Tooltip("Seconds between slide boosts. Sliding still works inside the cooldown, just without the boost.")]
    public float slideBoostCooldown = 1.5f;
    [Tooltip("Extra speed on top of slideBoost when the slide starts on landing (crouch held in the air). Shares the cooldown.")]
    public float landSlideBoost = 1f;
    [Tooltip("Fraction of the fall speed beyond a normal jump (drops off ledges) turned into forward slide speed.")]
    [Range(0f, 1f)] public float landSlideFallConversion = 0.35f;

    [Header("Bunny Hop")]
    [Tooltip("After landing, a jump inside this window is a perfect hop: no ground friction, speed is kept.")]
    public float bhopWindow = 0.12f;
    [Tooltip("Speed added on every perfect hop (m/s).")]
    public float bhopSpeedGain = 0.8f;
    [Tooltip("Hard cap for hop / air-strafe / slide speed (m/s).")]
    public float bhopMaxSpeed = 20f;
    [Tooltip("Speed gained per second in the air when strafing (A/D) and turning the mouse the same way.")]
    public float airStrafeGain = 3f;
    [Tooltip("Mouse turn rate (deg/s) that counts as a fully synced air strafe.")]
    public float airStrafeTurnRate = 150f;

    [Header("Wall Run")]
    public float wallRunSpeed = 11f;
    public float wallRunGravity = -5f;
    public float wallRunMaxTime = 1.8f;
    public float wallJumpUp = 7.5f;
    public float wallJumpSide = 6f;
    public float wallCheckDistance = 0.9f;
    public float minWallRunHeight = 0.9f;

    [Header("Mantle")]
    public float mantleReach = 1.9f;
    public float mantleDuration = 0.35f;

    [Header("Camera")]
    public float mouseSensitivity = 0.12f;
    public float minPitch = -85f;
    public float maxPitch = 85f;
    [Tooltip("Degrees per second at full right-stick deflection.")]
    public float gamepadLookSpeed = 180f;
    public float baseFov = 80f;
    public float sprintFov = 92f;
    [Tooltip("Extra FOV between sprint speed and bhopMaxSpeed.")]
    public float bhopExtraFov = 8f;
    public float wallRunTilt = 12f;

    [Header("World")]
    public float killY = -40f;

    /// <summary>Raised on every respawn. true = full restart (timer reset), false = checkpoint.</summary>
    public event Action<bool> Respawned;
    public event Action CheckpointReached;
    public event Action Finished;
    public event Action WasCaught;

    /// <summary>Ground jump, wall jump or ledge mantle started this frame.</summary>
    public event Action Jumped;

    /// <summary>Perfect bunny hop; argument is the current chain length (1, 2, 3...).</summary>
    public event Action<int> Hopped;
    /// <summary>Slide started on touchdown (crouch held in the air); argument = speed gained.</summary>
    public event Action<float> LandSlid;
    public int HopChain { get; private set; }

    public bool IsFinished { get; private set; }
    public bool IsGrounded => cc && cc.isGrounded;
    public bool IsSliding => sliding;
    public bool IsWallRunning => wallRunning;
    public bool IsMantling => mantling;
    public bool IsSprinting { get; private set; }
    public Vector2 MoveInput { get; private set; }
    public float HorizontalSpeed => new Vector2(velocity.x, velocity.z).magnitude;
    public Vector3 Velocity => velocity;
    public Vector3 SpawnPoint => spawnPos;
    public Vector3 EyePosition => cameraRoot ? cameraRoot.position : transform.position + Vector3.up * 1.6f;

    const int IgnoreRaycastLayer = 2;
    int mask;
    CharacterController cc;
    Vector3 velocity;
    float yaw, pitch, tilt;
    float lastGroundedTime = -10f, lastJumpPressTime = -10f, lastWallJumpTime = -10f;
    bool sliding, wallRunning, mantling, sprintLatched;
    Vector3 wallNormal;
    Collider lastWall;
    float wallRunTime;
    Vector3 mantleStart, mantleEnd;
    float mantleT, mantleExitSpeed;
    Vector3 spawnPos, startPos;
    float spawnYaw, startYaw;
    float forwardHeldTime;
    float camHeightVel;
    float landTime = -10f, yawDelta, lastSlideBoostTime = -10f;
    bool wasGrounded;
    InputAction moveAction, lookMouseAction, lookStickAction, jumpAction, sprintAction, crouchAction, restartAction, pauseAction;

    void CreateActions()
    {
        moveAction = new InputAction("Move", InputActionType.Value);
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        moveAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
        moveAction.AddBinding("<Gamepad>/leftStick");

        lookMouseAction = new InputAction("LookMouse", InputActionType.Value, "<Mouse>/delta");
        lookStickAction = new InputAction("LookStick", InputActionType.Value, "<Gamepad>/rightStick");

        jumpAction = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jumpAction.AddBinding("<Gamepad>/buttonSouth");

        sprintAction = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
        sprintAction.AddBinding("<Keyboard>/rightShift");
        sprintAction.AddBinding("<Gamepad>/leftStickPress");

        crouchAction = new InputAction("CrouchSlide", InputActionType.Button, "<Keyboard>/leftCtrl");
        crouchAction.AddBinding("<Keyboard>/c");
        crouchAction.AddBinding("<Mouse>/forwardButton");
        crouchAction.AddBinding("<Gamepad>/buttonEast");

        restartAction = new InputAction("Restart", InputActionType.Button, "<Keyboard>/r");
        restartAction.AddBinding("<Gamepad>/select");
        pauseAction = new InputAction("Pause", InputActionType.Button, "<Keyboard>/escape");
        pauseAction.AddBinding("<Gamepad>/start");
    }

    InputAction[] AllActions => new[] { moveAction, lookMouseAction, lookStickAction, jumpAction, sprintAction, crouchAction, restartAction, pauseAction };

    void OnDisable()
    {
        foreach (var a in AllActions) a?.Disable();
    }

    void OnDestroy()
    {
        foreach (var a in AllActions) a?.Dispose();
    }

    void Awake()
    {
        CreateActions();
        cc = GetComponent<CharacterController>();
        if (!cam) cam = GetComponentInChildren<Camera>();
        mask = ~(1 << IgnoreRaycastLayer);
        gameObject.layer = IgnoreRaycastLayer;
        startPos = spawnPos = transform.position;
        startYaw = spawnYaw = yaw = transform.eulerAngles.y;
    }

    void OnEnable()
    {
        foreach (var a in AllActions) a.Enable();
        LockCursor(true);
    }

    /// <summary>
    /// Same core as FirstPersonFighter (Mocap FPS): raw mouse / stick, yaw rotates the body, pitch rotates
    /// the camera pivot, both applied immediately with no smoothing.
    /// </summary>
    void Look(float dt)
    {
        yawDelta = 0f;
        if (Cursor.lockState != CursorLockMode.Locked) return;
        Vector2 look = lookMouseAction.ReadValue<Vector2>() * mouseSensitivity
                     + lookStickAction.ReadValue<Vector2>() * (gamepadLookSpeed * dt);
        yawDelta = look.x;
        transform.Rotate(0f, look.x, 0f);
        yaw = transform.eulerAngles.y;
        pitch = Mathf.Clamp(pitch - look.y, minPitch, maxPitch);
        if (cameraRoot) cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, tilt);
    }

    static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (pauseAction.WasPressedThisFrame()) LockCursor(false);
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked) LockCursor(true);
        if (restartAction.WasPressedThisFrame()) { FullRestart(); return; }

        Look(dt);

        Vector2 input = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        MoveInput = input;

        // Sprint: Auto (default) = run forward briefly and you sprint, no Shift needed — avoids
        // Shift+W+Space keyboard ghosting on many membrane/laptop keyboards. Shift always forces sprint.
        bool sprintHeld = sprintAction.IsPressed();
        if (sprintAction.WasPressedThisFrame()) sprintLatched = !sprintLatched;
        forwardHeldTime = input.y > 0.5f ? forwardHeldTime + dt : 0f;
        if (input.y <= 0.1f) sprintLatched = false;
        bool sprint = sprintMode switch
        {
            SprintMode.Toggle => sprintHeld || sprintLatched,
            SprintMode.Auto => sprintHeld || forwardHeldTime > autoSprintDelay,
            _ => sprintHeld,
        };
        IsSprinting = sprint;
        bool crouchHeld = crouchAction.IsPressed();
        bool crouchPressed = crouchAction.WasPressedThisFrame();
        if (jumpAction.WasPressedThisFrame()) lastJumpPressTime = Time.time;

        if (mantling)
        {
            UpdateMantle(dt);
            UpdateCamera(dt);
            return;
        }

        bool grounded = cc.isGrounded;
        if (grounded) { lastGroundedTime = Time.time; lastWall = null; }
        bool justLanded = grounded && !wasGrounded;
        float impactSpeed = justLanded ? Mathf.Max(0f, -velocity.y) : 0f; // velocity.y still holds the fall here
        if (justLanded) landTime = Time.time;
        wasGrounded = grounded;
        bool inHopWindow = grounded && Time.time - landTime < bhopWindow;
        if (grounded && !inHopWindow) HopChain = 0;

        Vector3 wish = transform.right * input.x + transform.forward * input.y;
        Vector3 hVel = new Vector3(velocity.x, 0f, velocity.z);

        // --- Crouch / slide ---
        // Any movement + crouch = slide, always along the look direction.
        // Crouch held in the air = slide the moment we touch down (Titanfall / Apex style landing slide).
        bool moving = hVel.magnitude > slideMoveThreshold || input.sqrMagnitude > 0.01f;
        if (grounded && !sliding && moving)
        {
            if (justLanded && crouchHeld) StartSlide(ref hVel, impactSpeed);
            else if (crouchPressed) StartSlide(ref hVel, 0f);
        }
        if (sliding && (!crouchHeld || hVel.magnitude < slideMinSpeed)) sliding = false;
        bool crouching = crouchHeld || sliding || !CanStand();
        SetHeight(crouching ? crouchHeight : standHeight, dt);

        // --- Wall run ---
        if (!grounded) CheckWallRun(input, hVel);
        else wallRunning = false;

        float targetSpeed = crouching ? crouchSpeed : (sprint ? sprintSpeed : walkSpeed);

        if (wallRunning)
        {
            wallRunTime += dt;
            Vector3 along = Vector3.Cross(wallNormal, Vector3.up);
            if (Vector3.Dot(along, transform.forward) < 0f) along = -along;
            hVel = along * wallRunSpeed - wallNormal * 2f;
            velocity.y = Mathf.Max(velocity.y + wallRunGravity * dt, -3f);
            if (wallRunTime > wallRunMaxTime) wallRunning = false;
        }
        else if (grounded)
        {
            if (sliding) hVel = Vector3.MoveTowards(hVel, Vector3.zero, slideFriction * dt);
            else if (inHopWindow && hVel.magnitude > targetSpeed) { } // bhop grace: no friction on the landing frames
            else hVel = Vector3.MoveTowards(hVel, wish * targetSpeed, groundAccel * dt);
            if (velocity.y < 0f) velocity.y = -2f;
        }
        else
        {
            if (wish.sqrMagnitude > 0.01f)
            {
                // Air control: steer, but never bleed momentum above the target speed.
                float before = hVel.magnitude;
                Vector3 desired = wish * Mathf.Max(sprint ? sprintSpeed : walkSpeed, before);
                bool braking = before > 0.1f && Vector3.Dot(wish.normalized, hVel / before) < -0.3f;
                // Steer: rotate the momentum toward the input so direction changes are quick at any speed.
                if (!braking && before > 0.1f)
                    hVel = Vector3.RotateTowards(hVel, wish.normalized * before, airTurnSpeed * Mathf.Deg2Rad * dt, 0f);
                hVel = Vector3.MoveTowards(hVel, desired, airAccel * dt);
                // Steering sideways must not shorten the vector (only pulling back brakes).
                if (!braking && hVel.sqrMagnitude > 0.0001f) hVel = hVel.normalized * Mathf.Max(hVel.magnitude, before);
            }

            // Air strafe: A/D + turning the mouse the same way builds speed (Source-style, but forgiving).
            float hSpeed = hVel.magnitude;
            if (Mathf.Abs(input.x) > 0.1f && hSpeed > 1f && hSpeed < bhopMaxSpeed && dt > 0f)
            {
                float yawRate = yawDelta / dt;
                float sync = Mathf.Sign(yawRate) == Mathf.Sign(input.x) ? Mathf.Clamp01(Mathf.Abs(yawRate) / airStrafeTurnRate) : 0f;
                hVel *= Mathf.Min(hSpeed + airStrafeGain * sync * dt, bhopMaxSpeed) / hSpeed;
            }
            velocity.y += gravity * dt;
        }

        // --- Jump / wall jump / mantle ---
        bool wantJump = Time.time - lastJumpPressTime < jumpBuffer;
        if (wantJump)
        {
            if (wallRunning)
            {
                hVel = hVel.normalized * Mathf.Max(hVel.magnitude, sprintSpeed) + wallNormal * wallJumpSide;
                velocity.y = wallJumpUp;
                wallRunning = false;
                lastWallJumpTime = Time.time;
                lastJumpPressTime = -10f;
                Jumped?.Invoke();
            }
            else if (TryMantle(hVel.magnitude))
            {
                lastJumpPressTime = -10f;
                Jumped?.Invoke();
            }
            else if (Time.time - lastGroundedTime < coyoteTime)
            {
                // Perfect hop = jump pressed (or buffered) right as we land while carrying speed.
                if (inHopWindow && landTime > 0f && hVel.magnitude > walkSpeed - 0.5f)
                {
                    HopChain++;
                    float s = Mathf.Min(hVel.magnitude + bhopSpeedGain, Mathf.Max(bhopMaxSpeed, hVel.magnitude));
                    hVel = hVel.normalized * s;
                    Hopped?.Invoke(HopChain);
                }
                velocity.y = Mathf.Sqrt(-2f * gravity * jumpHeight);
                lastGroundedTime = -10f;
                lastJumpPressTime = -10f;
                sliding = false;
                Jumped?.Invoke();
            }
        }
        else if (!grounded && !wallRunning && input.y > 0f && jumpAction.IsPressed() && velocity.y < 2f)
        {
            // Holding jump while pushing into a ledge mid-air grabs it.
            if (TryMantle(hVel.magnitude)) Jumped?.Invoke();
        }

        if (!mantling)
        {
            velocity.x = hVel.x;
            velocity.z = hVel.z;
            CollisionFlags flags = cc.Move(velocity * dt);
            if ((flags & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;
            if (!wallRunning && (flags & CollisionFlags.Sides) != 0)
            {
                // Only when we actually hit something sideways: drop the speed that went into the wall.
                // (Syncing every frame made steps and plank seams feel bumpy.)
                Vector3 real = cc.velocity;
                velocity.x = real.x;
                velocity.z = real.z;
            }
        }

        if (transform.position.y < killY) Respawn(false);
        UpdateCamera(dt);
    }

    /// <summary>
    /// Slide along the look direction. The boost has a cooldown (Apex uses 2 s) so slide spam can't
    /// stack speed forever; a landing slide also turns part of the fall into forward speed.
    /// </summary>
    void StartSlide(ref Vector3 hVel, float impactSpeed)
    {
        sliding = true;
        float current = hVel.magnitude;
        float speed = Mathf.Max(current, walkSpeed);
        float gained = 0f;
        if (Time.time - lastSlideBoostTime >= slideBoostCooldown)
        {
            gained = slideBoost + (impactSpeed > 0f ? landSlideBoost : 0f);
            lastSlideBoostTime = Time.time;
        }
        // Only a fall deeper than a normal jump pays out, so flat-ground hop spam gains nothing here.
        float jumpVel = Mathf.Sqrt(-2f * gravity * jumpHeight);
        gained += Mathf.Max(0f, impactSpeed - jumpVel) * landSlideFallConversion;
        speed = Mathf.Min(speed + gained, Mathf.Max(bhopMaxSpeed, current));
        hVel = transform.forward * speed;
        if (impactSpeed > 0f) LandSlid?.Invoke(speed - current);
    }

    void CheckWallRun(Vector2 input, Vector3 hVel)
    {
        if (!wallRunning && Time.time - lastWallJumpTime < 0.2f) return;
        if (input.y <= 0f || Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, mask, QueryTriggerInteraction.Ignore))
        {
            wallRunning = false;
            return;
        }

        Vector3 origin = transform.position + Vector3.up * 1f;
        float dist = cc.radius + wallCheckDistance;
        RaycastHit hit;
        bool found = Physics.Raycast(origin, transform.right, out hit, dist, mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.normal.y) < 0.2f;
        if (!found)
            found = Physics.Raycast(origin, -transform.right, out hit, dist, mask, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.normal.y) < 0.2f;

        if (!found) { wallRunning = false; return; }

        if (!wallRunning)
        {
            if (hit.collider == lastWall || hVel.magnitude < 4f) return;
            wallRunning = true;
            wallRunTime = 0f;
            lastWall = hit.collider;
            velocity.y = Mathf.Max(velocity.y, 1.5f);
        }
        wallNormal = hit.normal;
    }

    bool TryMantle(float currentSpeed)
    {
        Vector3 fwd = transform.forward;
        if (!Physics.Raycast(transform.position + Vector3.up * 0.5f, fwd, out RaycastHit wallHit, cc.radius + 0.7f, mask, QueryTriggerInteraction.Ignore))
            return false;
        if (wallHit.normal.y > 0.3f) return false;

        Vector3 probe = wallHit.point - wallHit.normal * 0.4f + Vector3.up * (mantleReach + 0.6f);
        if (Physics.CheckSphere(probe, 0.1f, mask, QueryTriggerInteraction.Ignore)) return false;
        if (!Physics.Raycast(probe, Vector3.down, out RaycastHit topHit, mantleReach + 0.6f, mask, QueryTriggerInteraction.Ignore))
            return false;
        if (topHit.normal.y < 0.7f) return false;

        float ledge = topHit.point.y - transform.position.y;
        if (ledge < 0.4f || ledge > mantleReach) return false;

        Vector3 end = topHit.point + Vector3.up * 0.05f;
        float r = cc.radius * 0.9f;
        if (Physics.CheckCapsule(end + Vector3.up * (r + 0.05f), end + Vector3.up * (crouchHeight - r), r, mask, QueryTriggerInteraction.Ignore))
            return false;

        mantling = true;
        sliding = false;
        wallRunning = false;
        mantleStart = transform.position;
        mantleEnd = end;
        mantleT = 0f;
        mantleExitSpeed = Mathf.Max(currentSpeed, walkSpeed) * 0.8f;
        velocity = Vector3.zero;
        return true;
    }

    void UpdateMantle(float dt)
    {
        mantleT += dt / mantleDuration;
        float t = Mathf.Clamp01(mantleT);
        Vector3 mid = new Vector3(mantleStart.x, mantleEnd.y, mantleStart.z);
        Vector3 p = t < 0.6f
            ? Vector3.Lerp(mantleStart, mid, Mathf.SmoothStep(0f, 1f, t / 0.6f))
            : Vector3.Lerp(mid, mantleEnd, (t - 0.6f) / 0.4f);
        Teleport(p);

        if (t >= 1f)
        {
            mantling = false;
            velocity = transform.forward * mantleExitSpeed;
            velocity.y = 0f;
        }
    }

    bool CanStand()
    {
        if (cc.height >= standHeight - 0.01f) return true;
        Vector3 p = transform.position;
        float r = cc.radius * 0.95f;
        return !Physics.CheckCapsule(p + Vector3.up * (standHeight * 0.5f), p + Vector3.up * (standHeight - r), r, mask, QueryTriggerInteraction.Ignore);
    }

    void SetHeight(float h, float dt)
    {
        float nh = Mathf.MoveTowards(cc.height, h, dt * 6f);
        cc.height = nh;
        cc.center = Vector3.up * (nh * 0.5f);
    }

    void UpdateCamera(float dt)
    {
        if (!cameraRoot) return;
        Vector3 lp = cameraRoot.localPosition;
        lp.y = Mathf.SmoothDamp(lp.y, cc.height - 0.15f, ref camHeightVel, 0.08f);
        cameraRoot.localPosition = lp;

        float targetTilt = 0f;
        if (wallRunning) targetTilt = Vector3.Dot(wallNormal, transform.right) < 0f ? wallRunTilt : -wallRunTilt;
        else if (sliding) targetTilt = 4f;
        tilt = Mathf.Lerp(tilt, targetTilt, dt * 8f);
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, tilt);

        if (cam)
        {
            float hs = new Vector2(velocity.x, velocity.z).magnitude;
            float speed01 = Mathf.InverseLerp(walkSpeed, sprintSpeed + 2f, hs);
            float over01 = Mathf.InverseLerp(sprintSpeed + 2f, bhopMaxSpeed, hs);
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, Mathf.Lerp(baseFov, sprintFov, speed01) + bhopExtraFov * over01, dt * 6f);
        }
    }

    void Teleport(Vector3 p)
    {
        cc.enabled = false;
        transform.position = p;
        cc.enabled = true;
    }

    public void SetCheckpoint(Vector3 pos, float yawDeg)
    {
        if ((pos - spawnPos).sqrMagnitude < 0.01f) return;
        spawnPos = pos;
        spawnYaw = yawDeg;
        CheckpointReached?.Invoke();
    }

    public void Respawn(bool full)
    {
        if (full)
        {
            spawnPos = startPos;
            spawnYaw = startYaw;
            IsFinished = false;
        }
        mantling = sliding = wallRunning = sprintLatched = false;
        HopChain = 0;
        landTime = lastSlideBoostTime = -10f;
        lastWall = null;
        velocity = Vector3.zero;
        forwardHeldTime = 0f;
        tilt = 0f;
        lastJumpPressTime = lastWallJumpTime = -10f;
        lastGroundedTime = Time.time;
        foreach (var a in AllActions) if (!a.enabled) a.Enable();
        cc.height = standHeight;
        cc.center = Vector3.up * (standHeight * 0.5f);
        Teleport(spawnPos);
        yaw = spawnYaw;
        pitch = 0f;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        Respawned?.Invoke(full);
    }

    public void FullRestart() => Respawn(true);

    public void Caught()
    {
        if (IsFinished) return;
        WasCaught?.Invoke();
        Respawn(true);
    }

    public void Finish()
    {
        if (IsFinished) return;
        IsFinished = true;
        Finished?.Invoke();
    }
}
