using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Control unificado del agente TIAGo++: articulaciones (ArticulationBody) + locomoción diferencial.
/// Refactorizado para separar responsabilidades de Input, Física y Control de Articulaciones.
/// </summary>
[DefaultExecutionOrder(10000)]
public class TiagoAgentController : MonoBehaviour
{
    #region Constants: Link Names
    static class LinkNames
    {
        public const string ShoulderPanR  = "arm_right_1_link";
        public const string ShoulderLiftR = "arm_right_2_link";
        public const string ElbowR        = "arm_right_3_link";
        public const string WristYawR     = "arm_right_6_link";
        public const string FingerR_Left  = "gripper_right_left_finger_link";
        public const string FingerR_Right = "gripper_right_right_finger_link";
        public const string TorsoLift     = "torso_lift_link";
        public const string HeadPan       = "head_1_link";
        public const string HeadTilt      = "head_2_link";
    }
    #endregion

    #region Serialized Fields (Inspector)
    // =========================
    //  SECCIÓN: JOINTS / POSES
    // =========================
    [Header("UI Demo — Torso")]
    [SerializeField] float torsoStep = 0.02f; // 2 cm

    // ============================
    //  SECCIÓN: LOCOMOCIÓN BASE
    // ============================
    [Header("Locomoción — Referencias")]
    public ArticulationBody wheelLeft;
    public ArticulationBody wheelRight;
    [Tooltip("ArticulationBody del eslabón base para medir vReal")]
    public ArticulationBody baseAB;

    [Header("Geometría (m)")]
    public float wheelRadius = 0.098f;
    public float wheelSeparation = 0.404f;

    // Presets
    public enum SpeedPreset { Conservative, Realistic, Agile, Arcade }
    [Header("Presets de velocidad")]
    public SpeedPreset preset = SpeedPreset.Agile;
    public bool applyPresetOnAwake = true;
#if UNITY_EDITOR
    [Tooltip("Sincroniza si cambias el preset en el editor")]
    public bool autoSyncInEditor = true;
#endif

    [Header("Límites actuales (derivan del preset)")]
    [Tooltip("Velocidad lineal máx (m/s)")]   public float maxV = 0.7f;
    [Tooltip("Velocidad angular máx (rad/s)")] public float maxW = 1.5f;
    [Tooltip("Respuesta/suavizado (~aceleración)")] public float accel = 10f;

    [Header("Modos turbo / lenta (teclas)")]
    [Tooltip("Numpad 7")] public float slowScale = 0.5f;
    [Tooltip("Numpad 9")] public float turboScale = 2.2f;
    public bool scaleAccelWithSpeed = true;

    [Header("Motor rueda")]
    public float wheelForceLimit = 2e6f;
    [Header("Signo de cada rueda")]
    public int leftSign = -1;
    public int rightSign = 1;

    public enum KeyScheme { Numpad8456, FunctionF6F9, CtrlPlusIJKL }
    public KeyScheme scheme = KeyScheme.Numpad8456;

    // ============================
    //  SECCIÓN: DEBUG / TELEMETRÍA
    // ============================
    [Header("Debug / Telemetría")]
    public bool debugTorsoStream = false;
    public int logEveryNFrames = 30;
    #endregion

    #region Internal State
    enum Axis { X, Y, Z }
    
    // Configuración estática de ejes por joint
    readonly Dictionary<string, Axis> _jointAxis = new()
    {
        { LinkNames.TorsoLift, Axis.Y },
        { LinkNames.HeadPan,   Axis.X },
        { LinkNames.HeadTilt,  Axis.X },
        { LinkNames.ShoulderPanR,  Axis.X },
        { LinkNames.ShoulderLiftR, Axis.X },
        { LinkNames.ElbowR,        Axis.X },
        { LinkNames.WristYawR,     Axis.X },
        { LinkNames.FingerR_Left,  Axis.X },
        { LinkNames.FingerR_Right, Axis.X },
    };

    // Caché de componentes ArticulationBody
    Dictionary<string, ArticulationBody> _jointsCache;

    // Estado de Input (normalizado)
    bool _inputFwd, _inputBack, _inputLeft, _inputRight, _inputTurbo, _inputSlow;

    // Estado de Dinámica
    float _currentVCmd; // Velocidad lineal actual (suavizada)
    float _currentWCmd; // Velocidad angular actual (suavizada)
    SpeedPreset _lastAppliedPreset;
    #endregion

    #region Unity Lifecycle
    void Awake()
    {
        InitializeJointsCache();
        InitializeWheels();

        if (applyPresetOnAwake) ApplyPreset(preset);
        _lastAppliedPreset = preset;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!Application.isPlaying && autoSyncInEditor) ApplyPreset(preset);
    }
#endif

    void Update()
    {
        HandleInput();
        HandleHotkeys();
    }

    void FixedUpdate()
    {
        if (debugTorsoStream) LogTorsoState();

        CheckAndApplyPresetRuntime();
        ProcessLocomotion();
    }
    #endregion

    #region Initialization
    void InitializeJointsCache()
    {
        _jointsCache = new Dictionary<string, ArticulationBody>();
        foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!_jointsCache.ContainsKey(ab.name))
                _jointsCache.Add(ab.name, ab);
        }
    }

    void InitializeWheels()
    {
        ConfigureWheelPhysics(wheelLeft);
        ConfigureWheelPhysics(wheelRight);
    }

    void ConfigureWheelPhysics(ArticulationBody wheel)
    {
        if (!wheel) return;
        var drive = wheel.xDrive;
        drive.driveType = ArticulationDriveType.Velocity;
        drive.stiffness = 0f;
        drive.damping = 0f;
        drive.forceLimit = wheelForceLimit;
        wheel.xDrive = drive;
        wheel.jointFriction = 0f;
    }
    #endregion

    #region Input Handling
    void HandleInput()
    {
        var kb = Keyboard.current;
        if (kb == null)
        {
            _inputFwd = _inputBack = _inputLeft = _inputRight = _inputTurbo = _inputSlow = false;
            return;
        }

        bool modCtrl = kb.rightCtrlKey.isPressed || kb.leftCtrlKey.isPressed;

        // Lectura según esquema
        if (scheme == KeyScheme.FunctionF6F9)
        {
            _inputFwd = kb.f7Key.isPressed; 
            _inputBack = kb.f9Key.isPressed; 
            _inputLeft = kb.f6Key.isPressed; 
            _inputRight = kb.f8Key.isPressed;
        }
        else if (scheme == KeyScheme.CtrlPlusIJKL)
        {
            _inputFwd = modCtrl && kb.iKey.isPressed; 
            _inputBack = modCtrl && kb.kKey.isPressed; 
            _inputLeft = modCtrl && kb.jKey.isPressed; 
            _inputRight = modCtrl && kb.lKey.isPressed;
        }
        else // Numpad8456 default
        {
            _inputFwd  = (kb.numpad8Key?.isPressed ?? false) || (kb.digit8Key?.isPressed ?? false) || (modCtrl && kb.iKey.isPressed);
            _inputBack = (kb.numpad5Key?.isPressed ?? false) || (kb.digit5Key?.isPressed ?? false) || (modCtrl && kb.kKey.isPressed);
            _inputLeft = (kb.numpad4Key?.isPressed ?? false) || (kb.digit4Key?.isPressed ?? false) || (modCtrl && kb.jKey.isPressed);
            _inputRight = (kb.numpad6Key?.isPressed ?? false) || (kb.digit6Key?.isPressed ?? false) || (modCtrl && kb.lKey.isPressed);
        }

        // Modificadores de velocidad
        _inputTurbo = (kb.numpad9Key?.isPressed ?? false) || (kb.pageUpKey?.isPressed ?? false) ||
                      (kb.f11Key?.isPressed ?? false) || (kb.numpadPlusKey?.isPressed ?? false) ||
                      (kb.tKey?.isPressed ?? false);

        _inputSlow = (kb.numpad7Key?.isPressed ?? false) || (kb.pageDownKey?.isPressed ?? false) ||
                     (kb.f10Key?.isPressed ?? false) || (kb.numpadMinusKey?.isPressed ?? false) ||
                     (kb.yKey?.isPressed ?? false);
    }

    void HandleHotkeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.f1Key.wasPressedThisFrame) preset = SpeedPreset.Conservative;
        if (kb.f2Key.wasPressedThisFrame) preset = SpeedPreset.Realistic;
        if (kb.f3Key.wasPressedThisFrame) preset = SpeedPreset.Agile;
        if (kb.f4Key.wasPressedThisFrame) preset = SpeedPreset.Arcade;
    }
    #endregion

    #region Locomotion Logic
    void ProcessLocomotion()
    {
        // 1. Calcular factor de escala
        float scale = _inputTurbo ? turboScale : (_inputSlow ? slowScale : 1f);

        // 2. Calcular objetivos (Targets)
        float vTarget = 0f, wTarget = 0f;
        if (_inputFwd)  vTarget += maxV * scale;
        if (_inputBack) vTarget -= maxV * scale;
        if (_inputLeft) wTarget += maxW * scale;
        if (_inputRight) wTarget -= maxW * scale;

        // 3. Calcular aceleración efectiva
        float effectiveAccel = scaleAccelWithSpeed ? accel * Mathf.Abs(scale) : accel;
        float dt = Time.fixedDeltaTime;

        // 4. Suavizado (MoveTowards)
        _currentVCmd = Mathf.MoveTowards(_currentVCmd, vTarget, effectiveAccel * dt);
        _currentWCmd = Mathf.MoveTowards(_currentWCmd, wTarget, effectiveAccel * dt);

        // 5. Aplicar a ruedas
        ApplyDifferentialDrive(_currentVCmd, _currentWCmd);
    }

    void ApplyDifferentialDrive(float v, float w)
    {
        // Cinemática inversa para drive diferencial
        float wL = (2f * v - w * wheelSeparation) / (2f * wheelRadius);
        float wR = (2f * v + w * wheelSeparation) / (2f * wheelRadius);

        // Aplicar signos de corrección de montaje
        wL *= leftSign;
        wR *= rightSign;

        SetWheelTargetVelocity(wheelLeft, wL);
        SetWheelTargetVelocity(wheelRight, wR);
    }

    void SetWheelTargetVelocity(ArticulationBody wheel, float omegaRadPerSec)
    {
        if (!wheel) return;

        var drive = wheel.xDrive;
        // Solo actualizamos la velocidad objetivo para evitar overhead de PhysX
        drive.targetVelocity = omegaRadPerSec * Mathf.Rad2Deg; // Unity usa grados/s
        wheel.xDrive = drive;
    }

    void CheckAndApplyPresetRuntime()
    {
        if (preset != _lastAppliedPreset)
        {
            ApplyPreset(preset);
            _lastAppliedPreset = preset;
            Debug.Log($"[Drive] Preset aplicado en runtime: {preset} (maxV={maxV}, maxW={maxW}, accel={accel})");
        }
    }

    void ApplyPreset(SpeedPreset p)
    {
        switch (p)
        {
            case SpeedPreset.Conservative: maxV = 0.4f; maxW = 0.8f; accel = 6f; break;
            case SpeedPreset.Realistic:    maxV = 0.7f; maxW = 1.5f; accel = 10f; break;
            case SpeedPreset.Agile:        maxV = 1.0f; maxW = 2.0f; accel = 14f; break;
            case SpeedPreset.Arcade:       maxV = 1.4f; maxW = 2.8f; accel = 20f; break;
        }
    }
    #endregion

    #region Joint Control Helpers
    ArticulationBody J(string linkName)
    {
        if (_jointsCache.TryGetValue(linkName, out var ab) && ab) return ab;
        
        // Fallback: búsqueda profunda si no estaba en caché inicial
        var t = FindDeep(transform, linkName);
        if (!t) return null;
        
        ab = t.GetComponent<ArticulationBody>();
        if (ab) _jointsCache[linkName] = ab;
        return ab;
    }

    static Transform FindDeep(Transform r, string name)
    {
        if (r.name == name) return r;
        for (int i = 0; i < r.childCount; i++)
        {
            var t = FindDeep(r.GetChild(i), name);
            if (t) return t;
        }
        return null;
    }

    Axis AxisOf(string link) => _jointAxis.TryGetValue(link, out var a) ? a : Axis.X;

    static ArticulationDrive GetDrive(ArticulationBody j, Axis a)
        => (a == Axis.X) ? j.xDrive : (a == Axis.Y) ? j.yDrive : j.zDrive;

    static void SetDrive(ArticulationBody j, Axis a, ArticulationDrive d)
    {
        if (a == Axis.X) j.xDrive = d;
        else if (a == Axis.Y) j.yDrive = d;
        else j.zDrive = d;
    }

    void SetTarget(ArticulationBody j, string linkName, float target)
    {
        if (!j) return;
        var axis = AxisOf(linkName);
        var d = GetDrive(j, axis);
        d.target = Mathf.Clamp(target, d.lowerLimit, d.upperLimit);
        SetDrive(j, axis, d);
    }

    void Bump(ArticulationBody j, string linkName, float delta)
    {
        if (!j) return;
        var axis = AxisOf(linkName);
        var d = GetDrive(j, axis);

        const float margin = 0.005f;
        float lo = d.lowerLimit + margin;
        float hi = d.upperLimit - margin;

        d.target = Mathf.Clamp(d.target + delta, lo, hi);
        SetDrive(j, axis, d);
    }
    #endregion

    #region Public API (Poses & Actions)
    public void PoseHome()
    {
        SetTarget(J(LinkNames.ShoulderPanR),  LinkNames.ShoulderPanR,  0f);
        SetTarget(J(LinkNames.ShoulderLiftR), LinkNames.ShoulderLiftR, 0f);
        SetTarget(J(LinkNames.ElbowR),        LinkNames.ElbowR,        0f);
        SetTarget(J(LinkNames.WristYawR),     LinkNames.WristYawR,     0f);

        SetTarget(J(LinkNames.TorsoLift), LinkNames.TorsoLift, 0.00f);
        SetTarget(J(LinkNames.HeadPan),   LinkNames.HeadPan,   0f);
        SetTarget(J(LinkNames.HeadTilt),  LinkNames.HeadTilt, -5f);
        CloseGripperR();
    }

    public void WaveRight()
    {
        SetTarget(J(LinkNames.ShoulderPanR),  LinkNames.ShoulderPanR,  25f);
        SetTarget(J(LinkNames.ShoulderLiftR), LinkNames.ShoulderLiftR, -35f);
        SetTarget(J(LinkNames.ElbowR),        LinkNames.ElbowR,        60f);
        SetTarget(J(LinkNames.WristYawR),     LinkNames.WristYawR,     30f);
    }

    public void TorsoUp()   => Bump(J(LinkNames.TorsoLift), LinkNames.TorsoLift, +torsoStep);
    public void TorsoDown() => Bump(J(LinkNames.TorsoLift), LinkNames.TorsoLift, -torsoStep);

    public void HeadLeft()  => Bump(J(LinkNames.HeadPan),  LinkNames.HeadPan,  -10f);
    public void HeadRight() => Bump(J(LinkNames.HeadPan),  LinkNames.HeadPan,  +10f);
    public void HeadUp()    => Bump(J(LinkNames.HeadTilt), LinkNames.HeadTilt, +8f);
    public void HeadDown()  => Bump(J(LinkNames.HeadTilt), LinkNames.HeadTilt, -8f);

    public void OpenGripperR()  => SetGripperRGap(0.07f);
    public void CloseGripperR() => SetGripperRGap(0.00f);

    void SetGripperRGap(float gapMeters)
    {
        var half = Mathf.Max(0f, gapMeters * 0.5f);
        SetTarget(J(LinkNames.FingerR_Left),  LinkNames.FingerR_Left,  half);
        SetTarget(J(LinkNames.FingerR_Right), LinkNames.FingerR_Right, half);
    }

    [ContextMenu("Dump Torso State (once)")]
    public void LogTorsoState()
    {
        var t = J(LinkNames.TorsoLift);
        if (!t) { Debug.Log("Torso no encontrado"); return; }
        
        var d = t.yDrive; // torso usa eje Y
        float q = (float)t.jointPosition[0];   // m
        float v = (float)t.jointVelocity[0];   // m/s

        Debug.Log(
            $"[TORSO] q={q:F4} m, v={v:F3} m/s | " +
            $"target={d.target:F3} m | limits=[{d.lowerLimit:F3},{d.upperLimit:F3}] | " +
            $"stiff={d.stiffness} damp={d.damping} forceLim={d.forceLimit}"
        );
    }
    #endregion
}
