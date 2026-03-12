using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    // ??????????????????????????????????????????????
    // INSPECTOR HEADERS
    // ??????????????????????????????????????????????

    [Header("Movement")]
    public float moveSpeed = 14f;
    public float sprintSpeed = 20f;
    public float groundAccel = 20f;
    public float groundFriction = 10f;
    public float jumpHeight = 1.9f;
    public float gravity = -20f;

    [Header("Air Strafing (Source-style)")]
    public float airAccel = 2.8f;
    public float airMaxSpeed = 0.2f;
    public float airTurnSpeed = 2.5f;

    [Header("Bhop")]
    [Tooltip("Landing while holding jump re-fires jump automatically, preserving speed")]
    public bool bhopEnabled = true;
    [Tooltip("Fraction of horizontal momentum kept when bhopping (1 = full preservation)")]
    [Range(0f, 1f)]
    public float bhopSpeedRetention = 0.98f;
    [Tooltip("Extra speed bonus stacked per consecutive successful bhop")]
    public float bhopStackBonus = 0.4f;
    [Tooltip("Maximum consecutive bhop stacks before bonus stops growing")]
    public int bhopMaxStacks = 6;

    [Header("Momentum")]
    public float maxMomentum = 18f;
    public float momentumDrag = 4.5f;
    public float momentumDragCurve = 1.4f;

    [Header("Boost – General")]
    public float boostBaseForce = 2.5f;
    public float boostMaxForce = 11f;
    public float boostFlickScale = 0.048f;
    public float boostHopForce = 8.0f;
    public float boostThreshold = 5.0f;
    public float boostCooldown = 0.18f;

    [Header("Boost – Reverse Juke")]
    [Tooltip("Dot product threshold below which a flick is treated as a reverse")]
    public float reverseThreshold = -0.4f;
    [Tooltip("Fraction of old momentum wiped instantly on reverse")]
    [Range(0f, 1f)]
    public float reverseCancelRatio = 0.92f;
    [Tooltip("Extra force multiplier when juking in reverse")]
    public float reverseForceBonus = 1.35f;

    [Header("Boost – Horizontal Flick")]
    [Tooltip("Extra force multiplier for left/right mouse-flick boosts")]
    public float horizontalFlickBonus = 1.25f;
    [Tooltip("How much lateral boost blends with existing momentum (0=pure replace, 1=pure add)")]
    [Range(0f, 1f)]
    public float lateralBlend = 0.15f;

    [Header("Boost – Wall")]
    public float wallBoostMultiplier = 1.7f;
    public float wallCheckDistance = 1.3f;
    public LayerMask wallMask;

    [Header("Wall Jump")]
    public float wallJumpUpForce = 8f;
    public float wallJumpAwayForce = 9f;
    public float wallJumpMomentumWipe = 0.5f;
    public float wallJumpCooldown = 0.25f;
    public float wallJumpCheckRadius = 1.1f;
    public int wallJumpRayCount = 8;
    [Tooltip("How much inbound horizontal momentum amplifies the wall-jump away force")]
    public float wallJumpMomentumBonus = 0.35f;
    [Tooltip("Max extra away-force from momentum bonus")]
    public float wallJumpMomentumBonusCap = 8f;

    [Header("Wall Climb (RMB + down-to-up flick near wall)")]
    [Tooltip("Upward force per wall-climb flick")]
    public float wallClimbUpForce = 14f;
    [Tooltip("Small push away from wall so you don't clip into it")]
    public float wallClimbAwayNudge = 2.5f;
    [Tooltip("Flick must be mostly upward: rawY speed minimum")]
    public float wallClimbFlickMin = 3.0f;
    [Tooltip("How much the flick speed scales the climb force")]
    public float wallClimbFlickScale = 0.012f;
    [Tooltip("Max wall-climb force from scaling")]
    public float wallClimbMaxForce = 22f;
    [Tooltip("How close to a wall the player must be to trigger climb")]
    public float wallClimbDetectDist = 1.3f;
    [Tooltip("Cooldown between individual climb pulses (s)")]
    public float wallClimbCooldown = 0.15f;
    [Tooltip("Gravity reduction while actively wall-climbing (0=full gravity, 1=zero-g)")]
    [Range(0f, 1f)]
    public float wallClimbGravityMult = 0.25f;

    [Header("Coyote / Buffer")]
    public float coyoteTime = 0.12f;
    public float jumpBufferTime = 0.12f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 5f;
    public Transform cameraTransform;

    // ??????????????????????????????????????????????
    // PRIVATE STATE
    // ??????????????????????????????????????????????

    CharacterController controller;

    Vector3 horizontalVelocity;
    Vector3 horizontalMomentum;
    float verticalVelocity;

    float xRotation;
    float boostTimer;
    float wallJumpTimer;
    float wallClimbTimer;
    float coyoteTimer;
    float jumpBufferTimer;

    // Bhop state
    int bhopStack;
    bool lastFrameGrounded;

    // Flick smoothing
    const int FLICK_SAMPLES = 3;
    float[] flickX = new float[FLICK_SAMPLES];
    float[] flickY = new float[FLICK_SAMPLES];
    int flickIdx;

    // Wall-climb tracks the previous frame's rawY to detect down?up transition
    float prevRawY;

    // ?? RMB must-release gate ??????????????????????
    // boostReady:      RMB was fully released since the last boost fired.
    //                  Prevents holding RMB to spam boosts or wall-climb.
    // wallClimbReady:  same gate applied independently to wall-climb so that
    //                  a boost and a climb on the same press don't conflict.
    bool boostReady = true;
    bool wallClimbReady = true;

    // ??????????????????????????????????????????????
    // INIT
    // ??????????????????????????????????????????????

    void Start()
    {
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ??????????????????????????????????????????????
    // UPDATE
    // ??????????????????????????????????????????????

    void Update()
    {
        HandleMouseLook();
        SampleFlick();
        UpdateRmbReadyState();   // must run before boost / climb checks
        HandleWallClimb();
        HandleBoost();
        HandleMovement();
        DecayMomentum();
    }

    // ??????????????????????????????????????????????
    // RMB READY-STATE
    // Both gates reset the moment RMB is fully released.
    // They are consumed (set false) individually when each action fires.
    // ??????????????????????????????????????????????

    void UpdateRmbReadyState()
    {
        if (!Input.GetMouseButton(1))
        {
            boostReady = true;
            wallClimbReady = true;
        }
    }

    // ??????????????????????????????????????????????
    // MOUSE LOOK
    // ??????????????????????????????????????????????

    void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * 100f * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * 100f * Time.deltaTime;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    // ??????????????????????????????????????????????
    // FLICK SAMPLING (rolling average, 3 frames)
    // ??????????????????????????????????????????????

    void SampleFlick()
    {
        flickX[flickIdx] = Input.GetAxisRaw("Mouse X") / Mathf.Max(Time.deltaTime, 0.001f);
        flickY[flickIdx] = Input.GetAxisRaw("Mouse Y") / Mathf.Max(Time.deltaTime, 0.001f);
        flickIdx = (flickIdx + 1) % FLICK_SAMPLES;
    }

    float SmoothedFlickX() { float s = 0f; foreach (float v in flickX) s += v; return s / FLICK_SAMPLES; }
    float SmoothedFlickY() { float s = 0f; foreach (float v in flickY) s += v; return s / FLICK_SAMPLES; }

    // ??????????????????????????????????????????????
    // WALL CLIMB
    // Triggered by: RMB held + airborne + near wall + down-to-up mouse flick.
    // Requires wallClimbReady (RMB must have been released since last climb).
    // ??????????????????????????????????????????????

    void HandleWallClimb()
    {
        wallClimbTimer -= Time.deltaTime;

        float rawY = Input.GetAxisRaw("Mouse Y");

        // Detect a down?up flick transition
        bool upFlick = prevRawY < -0.01f && rawY > 0.01f;
        float flickSpeed = rawY / Mathf.Max(Time.deltaTime, 0.001f);

        prevRawY = rawY;

        if (!Input.GetMouseButton(1)) return;
        if (!wallClimbReady) return;          // ? must release RMB between climbs
        if (controller.isGrounded) return;
        if (wallClimbTimer > 0f) return;
        if (!upFlick) return;
        if (flickSpeed < wallClimbFlickMin) return;

        Vector3 wallNormal = Vector3.zero;
        bool nearWall = FindNearestWall(wallClimbDetectDist, out wallNormal);
        if (!nearWall) return;

        float climbForce = Mathf.Clamp(
            wallClimbUpForce + flickSpeed * wallClimbFlickScale,
            wallClimbUpForce, wallClimbMaxForce);

        Vector3 awayDir = Vector3.ProjectOnPlane(wallNormal, Vector3.up).normalized;

        if (verticalVelocity < 0f) verticalVelocity = 0f;

        verticalVelocity += climbForce;
        horizontalVelocity = awayDir * wallClimbAwayNudge;

        wallClimbTimer = wallClimbCooldown;
        wallClimbReady = false;   // ? consume — must release RMB before next climb
    }

    // ??????????????????????????????????????????????
    // BOOST (ground only, fully horizontal)
    // Requires boostReady (RMB must have been released since last boost).
    // ??????????????????????????????????????????????

    void HandleBoost()
    {
        boostTimer -= Time.deltaTime;

        if (!controller.isGrounded) return;
        if (!Input.GetMouseButton(1)) return;
        if (!boostReady) return;              // ? must release RMB between boosts
        if (boostTimer > 0f) return;

        float sx = SmoothedFlickX();
        float sy = SmoothedFlickY();
        float speedX = Mathf.Abs(sx);
        float speedY = Mathf.Abs(sy);
        float maxSpd = Mathf.Max(speedX, speedY);

        if (maxSpd < boostThreshold) return;

        Vector3 camFwd = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
        Vector3 camRight = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;

        bool isHorizontal = speedX > speedY;

        Vector3 boostDir;
        float force = Mathf.Clamp(boostBaseForce + maxSpd * boostFlickScale,
                                    boostBaseForce, boostMaxForce);

        if (!isHorizontal)
        {
            boostDir = sy < 0 ? camFwd : -camFwd;
        }
        else
        {
            boostDir = sx > 0 ? -camRight : camRight;
            force *= horizontalFlickBonus;
        }

        if (Physics.Raycast(transform.position, transform.forward,
                            out RaycastHit hit, wallCheckDistance, wallMask))
        {
            boostDir = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
            force *= wallBoostMultiplier;
        }

        boostDir.Normalize();

        float dot = horizontalMomentum.magnitude > 0.1f
                    ? Vector3.Dot(horizontalMomentum.normalized, boostDir)
                    : 1f;

        if (dot < reverseThreshold)
        {
            horizontalMomentum *= (1f - reverseCancelRatio);
            horizontalMomentum += boostDir * force * reverseForceBonus;
        }
        else if (isHorizontal)
        {
            Vector3 preserved = horizontalMomentum * (1f - lateralBlend);
            horizontalMomentum = preserved + boostDir * force;
        }
        else
        {
            horizontalMomentum += boostDir * force;
        }

        if (horizontalMomentum.magnitude > maxMomentum)
            horizontalMomentum = horizontalMomentum.normalized * maxMomentum;

        verticalVelocity = boostHopForce;
        boostTimer = boostCooldown;
        boostReady = false;  // ? consume — must release RMB before next boost
    }

    // ??????????????????????????????????????????????
    // MOVEMENT
    // ??????????????????????????????????????????????

    void HandleMovement()
    {
        bool grounded = controller.isGrounded;

        // ?? Coyote ??
        if (grounded) coyoteTimer = coyoteTime;
        else coyoteTimer -= Time.deltaTime;

        // ?? Jump buffer — press only, not hold ??
        if (Input.GetButtonDown("Jump"))
            jumpBufferTimer = jumpBufferTime;
        else
            jumpBufferTimer -= Time.deltaTime;

        // ?? Landing logic ??
        bool justLanded = grounded && !lastFrameGrounded;

        if (grounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        // ?? Bhop: re-jump only if a recent press is still in the buffer ??
        if (bhopEnabled && justLanded && jumpBufferTimer > 0f)
        {
            bhopStack = Mathf.Min(bhopStack + 1, bhopMaxStacks);
            float speedMult = bhopSpeedRetention + bhopStackBonus * bhopStack * 0.05f;
            horizontalMomentum *= speedMult;
            if (horizontalMomentum.magnitude > maxMomentum * 1.3f)
                horizontalMomentum = horizontalMomentum.normalized * maxMomentum * 1.3f;

            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            jumpBufferTimer = 0f;
            coyoteTimer = 0f;
        }
        else if (justLanded)
        {
            bhopStack = 0;
        }

        lastFrameGrounded = grounded;

        // ?? WASD ??
        float inputX = Input.GetAxis("Horizontal");
        float inputZ = Input.GetAxis("Vertical");
        Vector3 wishDir = transform.right * inputX + transform.forward * inputZ;
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        if (grounded)
        {
            bool sprinting = Input.GetKey(KeyCode.LeftShift) && boostTimer <= 0f;
            float targetSpeed = sprinting ? sprintSpeed : moveSpeed;

            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, wishDir * targetSpeed, groundAccel * Time.deltaTime);
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, Vector3.zero, groundFriction * Time.deltaTime);
        }
        else
        {
            float curSpd = horizontalVelocity.magnitude;
            float addSpd = Mathf.Clamp(
                Vector3.Dot(wishDir * moveSpeed - horizontalVelocity, wishDir),
                0f, airMaxSpeed * moveSpeed);
            horizontalVelocity += wishDir * addSpd * airAccel * Time.deltaTime;

            if (wishDir.sqrMagnitude > 0.01f)
                horizontalVelocity = Vector3.Slerp(
                    horizontalVelocity,
                    wishDir * Mathf.Max(curSpd, moveSpeed * 0.5f),
                    airTurnSpeed * Time.deltaTime * 0.3f);
        }

        // ?? Standard jump (non-bhop) ??
        if (!bhopEnabled || !justLanded)
        {
            if (jumpBufferTimer > 0f && coyoteTimer > 0f)
            {
                verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                jumpBufferTimer = 0f;
                coyoteTimer = 0f;
                bhopStack = 0;
            }
            else if (jumpBufferTimer > 0f && !grounded && wallJumpTimer <= 0f)
            {
                TryWallJump();
            }
        }

        wallJumpTimer -= Time.deltaTime;

        // Reduce gravity while wall climbing
        bool nearWallClimb = !grounded && Input.GetMouseButton(1) && wallClimbTimer <= wallClimbCooldown * 0.5f;
        float gravThisFrame = nearWallClimb
            ? gravity * (1f - wallClimbGravityMult)
            : gravity;

        verticalVelocity += gravThisFrame * Time.deltaTime;

        controller.Move(
            (horizontalVelocity + horizontalMomentum + Vector3.up * verticalVelocity)
            * Time.deltaTime);
    }

    // ??????????????????????????????????????????????
    // WALL JUMP
    // ??????????????????????????????????????????????

    void TryWallJump()
    {
        Vector3 wallNormal = Vector3.zero;
        if (!FindNearestWall(wallJumpCheckRadius, out wallNormal)) return;

        Vector3 pushDir = Vector3.ProjectOnPlane(wallNormal, Vector3.up).normalized;

        float inboundSpeed = Mathf.Max(0f, -Vector3.Dot(
            (horizontalMomentum + horizontalVelocity).normalized,
            pushDir));
        float momentumBonus = Mathf.Min(
            inboundSpeed * (horizontalMomentum + horizontalVelocity).magnitude * wallJumpMomentumBonus,
            wallJumpMomentumBonusCap);

        horizontalMomentum *= (1f - wallJumpMomentumWipe);
        horizontalVelocity = pushDir * (wallJumpAwayForce + momentumBonus);

        verticalVelocity = wallJumpUpForce;
        jumpBufferTimer = 0f;
        wallJumpTimer = wallJumpCooldown;
    }

    // ??????????????????????????????????????????????
    // SHARED WALL DETECTION UTILITY
    // ??????????????????????????????????????????????

    bool FindNearestWall(float radius, out Vector3 normal)
    {
        normal = Vector3.zero;
        float bestDist = float.MaxValue;
        bool found = false;

        Vector3[] origins = new Vector3[]
        {
            transform.position,
            transform.position + Vector3.up * (controller.height * 0.4f)
        };

        for (int i = 0; i < wallJumpRayCount; i++)
        {
            float angle = i * (360f / wallJumpRayCount) * Mathf.Deg2Rad;
            Vector3 localDir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 worldDir = transform.TransformDirection(localDir);

            foreach (Vector3 origin in origins)
            {
                if (Physics.Raycast(origin, worldDir, out RaycastHit hit, radius, wallMask)
                    && hit.distance < bestDist)
                {
                    bestDist = hit.distance;
                    normal = hit.normal;
                    found = true;
                }
            }
        }

        return found;
    }

    // ??????????????????????????????????????????????
    // MOMENTUM DECAY (speed-dependent drag curve)
    // ??????????????????????????????????????????????

    void DecayMomentum()
    {
        if (horizontalMomentum.sqrMagnitude < 0.001f)
        {
            horizontalMomentum = Vector3.zero;
            return;
        }

        float speed = horizontalMomentum.magnitude;

        float curveDrag = momentumDrag * Mathf.Pow(speed / maxMomentum, momentumDragCurve);
        float flatDrag = controller.isGrounded ? groundFriction : 1.2f;

        float totalDecay = (curveDrag * speed + flatDrag) * Time.deltaTime;

        horizontalMomentum = Vector3.MoveTowards(
            horizontalMomentum, Vector3.zero, totalDecay);
    }
}