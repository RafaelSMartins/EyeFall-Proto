using UnityEngine;
using UnityEngine.UI;

/// <summary>Minimal HUD built at runtime: timer + subtitle at the top, dot crosshair, centre messages.</summary>
public class ParkourHUD : MonoBehaviour
{
    public ParkourController player;
    public string subtitle = "DON'T GET CAUGHT";

    [Header("Speedometer")]
    public bool showSpeedometer = true;
    [Tooltip("Speed that fills the bar (m/s). Defaults to the player's bhopMaxSpeed when 0.")]
    public float speedoMax = 0f;

    Gradient speedColors;

    Text timerText, subText, centerText, debugText;
    Text speedText, peakText, hopText;
    RectTransform speedFill, speedRoot;
    Image speedFillImage;
    const float BarWidth = 420f;
    float shownSpeed, peakSpeed, hopUntil, speedPunch;
    float elapsed;
    bool running = true;
    float messageUntil;

    void Start()
    {
        if (!player) player = FindAnyObjectByType<ParkourController>();
        BuildUI();
        if (player)
        {
            player.Respawned += full => { if (full) { elapsed = 0f; running = true; peakSpeed = 0f; } };
            player.Hopped += OnHop;
            player.LandSlid += gained => ShowMove(gained > 0.05f ? $"SLIDE +{gained:0.0}" : "SLIDE", gained > 0.05f);
            player.CheckpointReached += () => Flash("CHECKPOINT", new Color(0.95f, 0.9f, 0.8f), 1.2f);
            player.WasCaught += () => Flash("CAUGHT", new Color(0.9f, 0.15f, 0.12f), 2f);
            player.Finished += () =>
            {
                running = false;
                Flash("ESCAPED  " + Format(elapsed) + "\n<size=26>R to run again</size>", new Color(1f, 0.92f, 0.75f), float.MaxValue);
            };
        }
    }

    void BuildUI()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasGo = new GameObject("HUD Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        timerText = MakeText(canvasGo.transform, font, 64, new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(600, 80));
        subText = MakeText(canvasGo.transform, font, 26, new Vector2(0.5f, 1f), new Vector2(0, -112), new Vector2(800, 40));
        subText.text = subtitle;
        centerText = MakeText(canvasGo.transform, font, 54, new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(1200, 200));
        centerText.enabled = false;

        debugText = MakeText(canvasGo.transform, font, 20, new Vector2(0f, 0f), new Vector2(420, 70), new Vector2(800, 120));
        debugText.alignment = TextAnchor.LowerLeft;
        debugText.enabled = false;

        var dot = new GameObject("Crosshair", typeof(RectTransform), typeof(Image));
        dot.transform.SetParent(canvasGo.transform, false);
        var rt = (RectTransform)dot.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(6, 6);
        dot.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.85f);

        if (showSpeedometer) BuildSpeedometer(canvasGo.transform, font);
    }

    // ------------------------------------------------------------------ speedometer

    void BuildSpeedometer(Transform canvas, Font font)
    {
        {
            speedColors = new Gradient();
            speedColors.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.95f, 0.93f, 0.9f), 0f),
                    new GradientColorKey(new Color(1f, 0.85f, 0.3f), 0.5f),
                    new GradientColorKey(new Color(1f, 0.5f, 0.15f), 0.75f),
                    new GradientColorKey(new Color(1f, 0.2f, 0.25f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        }

        speedRoot = new GameObject("Speedometer", typeof(RectTransform)).GetComponent<RectTransform>();
        speedRoot.SetParent(canvas, false);
        speedRoot.anchorMin = speedRoot.anchorMax = new Vector2(0.5f, 0f);
        speedRoot.anchoredPosition = new Vector2(0f, 90f);

        speedText = MakeText(speedRoot, font, 56, new Vector2(0.5f, 0.5f), new Vector2(0f, 38f), new Vector2(400, 70));
        hopText = MakeText(speedRoot, font, 30, new Vector2(0.5f, 0.5f), new Vector2(0f, 96f), new Vector2(400, 40));
        hopText.enabled = false;

        Rect("Bar BG", speedRoot, new Vector2(0f, 0f), new Vector2(BarWidth + 6f, 14f), new Color(0f, 0f, 0f, 0.4f));
        speedFill = Rect("Bar Fill", speedRoot, new Vector2(-BarWidth * 0.5f, 0f), new Vector2(0f, 8f), Color.white);
        speedFill.pivot = new Vector2(0f, 0.5f);
        speedFillImage = speedFill.GetComponent<Image>();

        peakText = MakeText(speedRoot, font, 18, new Vector2(0.5f, 0.5f), new Vector2(BarWidth * 0.5f + 60f, 0f), new Vector2(120, 24));
        peakText.alignment = TextAnchor.MiddleLeft;
        peakText.color = new Color(0.95f, 0.93f, 0.9f, 0.7f);

        // Reference ticks: walk and sprint speed.
        if (player)
        {
            Tick(player.walkSpeed, "walk", font);
            Tick(player.sprintSpeed, "sprint", font);
        }
    }

    float SpeedoMax => speedoMax > 0f ? speedoMax : (player ? player.bhopMaxSpeed : 20f);

    void Tick(float speed, string label, Font font)
    {
        float x = -BarWidth * 0.5f + BarWidth * Mathf.Clamp01(speed / SpeedoMax);
        Rect("Tick " + label, speedRoot, new Vector2(x, 0f), new Vector2(2f, 20f), new Color(1f, 1f, 1f, 0.55f));
        var t = MakeText(speedRoot, font, 14, new Vector2(0.5f, 0.5f), new Vector2(x, -20f), new Vector2(80, 20));
        t.text = label;
        t.color = new Color(1f, 1f, 1f, 0.55f);
    }

    static RectTransform Rect(string name, Transform parent, Vector2 pos, Vector2 size, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
        return rt;
    }

    void OnHop(int chain) => ShowMove(chain > 1 ? $"BHOP x{chain}" : "BHOP", true);

    void ShowMove(string label, bool punch)
    {
        if (!hopText) return;
        hopText.text = label;
        hopText.enabled = true;
        hopUntil = Time.time + 0.9f;
        if (punch) speedPunch = 1f;
    }

    void UpdateSpeedometer()
    {
        if (!speedText || !player) return;
        float dt = Time.deltaTime;
        float speed = player.HorizontalSpeed;
        shownSpeed = Mathf.Lerp(shownSpeed, speed, 1f - Mathf.Exp(-14f * dt));
        peakSpeed = Mathf.Max(peakSpeed, speed);

        float t = Mathf.Clamp01(shownSpeed / SpeedoMax);
        Color c = speedColors.Evaluate(t);
        speedText.text = $"{shownSpeed:0.0}<size=22> m/s</size>";
        speedText.color = c;
        speedFill.sizeDelta = new Vector2(BarWidth * t, 8f);
        speedFillImage.color = c;
        peakText.text = $"max {peakSpeed:0.0}";

        // Little scale punch on every perfect hop.
        speedPunch = Mathf.MoveTowards(speedPunch, 0f, dt * 4f);
        speedText.rectTransform.localScale = Vector3.one * (1f + 0.18f * speedPunch * speedPunch);

        if (hopText.enabled)
        {
            float left = hopUntil - Time.time;
            if (left <= 0f) hopText.enabled = false;
            else
            {
                var hc = speedColors.Evaluate(Mathf.Max(t, 0.5f));
                hc.a = Mathf.Clamp01(left / 0.3f);
                hopText.color = hc;
            }
        }
    }

    static Text MakeText(Transform parent, Font font, int size, Vector2 anchor, Vector2 pos, Vector2 box)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(Shadow));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = box;
        var t = go.GetComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = new Color(0.95f, 0.93f, 0.9f);
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        go.GetComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.5f);
        return t;
    }

    public void Flash(string msg, Color color, float seconds)
    {
        if (!centerText) return;
        centerText.text = msg;
        centerText.color = color;
        centerText.enabled = true;
        messageUntil = seconds == float.MaxValue ? float.MaxValue : Time.time + seconds;
    }

    static string Format(float t)
    {
        int s = Mathf.FloorToInt(t);
        return $"{s / 60:00}:{s % 60:00}";
    }

    void Update()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.f3Key.wasPressedThisFrame && debugText) debugText.enabled = !debugText.enabled;
        if (debugText && debugText.enabled && player)
        {
            debugText.text =
                $"pos {player.transform.position:F1}  speed {player.HorizontalSpeed:F1}  vy {player.Velocity.y:F1}  hops {player.HopChain}\n" +
                $"input {player.MoveInput:F2}  sprint {player.IsSprinting}\n" +
                $"grounded {player.IsGrounded}  slide {player.IsSliding}  wall {player.IsWallRunning}  mantle {player.IsMantling}\n" +
                $"game view focus {Application.isFocused}  cursor {Cursor.lockState}";
        }

        UpdateSpeedometer();
        if (running) elapsed += Time.deltaTime;
        if (timerText) timerText.text = Format(elapsed);
        if (centerText && centerText.enabled && Time.time > messageUntil) centerText.enabled = false;
        if (running && centerText && messageUntil == float.MaxValue) centerText.enabled = false;
    }
}
