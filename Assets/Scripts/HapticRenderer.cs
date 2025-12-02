using System;
using Bhaptics.SDK2;
using UnityEngine;

public class HapticRenderer : MonoBehaviour
{
    public enum HapticDeviceType
    {
        Vest,
        GloveLeft,
        GloveRight
    }

    [Serializable]
    public class Actuator
    {
        [Tooltip("이 액추에이터가 속한 장비")]
        public HapticDeviceType targetDevice = HapticDeviceType.Vest;

        [Tooltip("bHaptics 모터 인덱스 (0 ~ motorCount-1)")]
        public int index;

        [Tooltip("이 모터의 월드 위치(벨 대비 리스너 위치로 사용)")]
        public Transform transform;

        [Tooltip("개별 게인(기본 1)")]
        public float localGain = 1f;

        [Tooltip("체크하면 손으로 간주")]
        public bool isHand = false; 
    }

    [Header("State Link")]
    [Tooltip("서버로부터 위치/접촉 정보를 받는 컴포넌트")]
    public PositionFollower positionFollower;

    [Header("Input")]
    public PiezoReader piezo;  

    [Header("Bell / Actuators")]
    public BellBeatRenderer bell;
    [Tooltip("사용할 모터들을 인덱스와 함께 등록 (예: 0~39)")]
    public Actuator[] actuators;

    [Header("Target Striker")]
    public Transform strikerImpactPoint;    

    [Header("Device Settings")]
    // [변경] 장비별 모터 개수 하드코딩 혹은 설정
    public int vestMotorCount = 40;
    public int gloveMotorCount = 6;

    [Header("Timing")]
    [Tooltip("한 프레임 전송 간격(초). 0 이면 bell.intervalTime 사용")]
    public float intervalTime = 0.0f;       // bHaptics 권장 최소 0.1s
    [Tooltip("히트 후 재생할 길이(초). 0 이면 bell.preCalculateSeconds 전체")]
    public float playLength = 0f;
    [Tooltip("히트가 새로 오면 기존 재생을 끊고 처음부터 재시작")]
    public bool restartOnNewHit = true;

    [Header("Level mapping (NO normalization)")]
    [Tooltip("최종레벨 = bell.EvaluateHaptic01(...) * gain * localGain * hitGain")]
    public float gain = 1.0f;
    [Range(0f, 1f)] public float levelCap = 0.8f;       // 상한
    [Range(0f, 0.2f)] public float silenceBelow = 0f;   // 이 값 미만이면 0

    [Header("Hit strength to gain")]
    [Tooltip("PiezoReader.hitThreshold → 0, maxStrength → 1 로 정규화")]
    public bool useHitStrength = false;  
    public float fixedHitGain = 1.0f;   
    public int maxStrength = 700;
    public float strengthScale = 1.0f;
    public float minHitGain = 0.2f;

    [Header("Bell evaluation")]
    [Tooltip("벨 방향성 감쇠 사용 여부 (BellBeatRenderer.GetAmplitudeAt의 인자)")]
    public bool useDirectivity = true;

    [Tooltip("손이 비접촉 상태일 때, 몸통 대비 공기 진동을 얼마나 느낄지 비율 (0.0 ~ 1.0)")]
    [Range(0f, 1f)] public float handAirMultiplier = 1f;

    [Header("Floor shaping")]
    [Tooltip("최종 레벨에서 공통 바닥(0..1)을 감산합니다. 예: 0.15 ~ 0.25")]
    [Range(0f, 0.5f)] public float subtractFloor01 = 0.1f;

    [Tooltip("감산 후 (level - floor)/(1-floor)로 0..1 재정규화")]
    public bool renormalizeAfterCut = false;

    [Header("Send Mode")]
    [Tooltip("프레임마다 바로 보냄 (deltaTime 기반). 끊김 방지용")]
    public bool frameDrivenSend = true;

    [Tooltip("한 패킷의 최소 지속시간(ms) — bHaptics 권장 최소 100ms")]
    public int minPacketMs = 100;

    [Tooltip("1프레임이 너무 길 때, 이 길이보다 큰 부분은 여러 청크로 나눠 보냄(ms)")]
    public int maxChunkMs = 150;

    [Header("Auto Play")]
    [Tooltip("Awake/Start 시 자동 재생")]
    public bool playOnAwake = false;

    [Tooltip("PlayOnAwake까지 대기(초)")]
    public float playOnAwakeDelay = 0f;

    [Tooltip("useHitStrength=false일 때 사용할 고정 게인")]
    public float playOnAwakeFixedGain = 1.0f;

    [Tooltip("useHitStrength=true일 때 사용할 가상 strength 값")]
    public int playOnAwakeStrength = 400;

    [Header("Debug Logs")]
    public bool debugLogs = false;        // 전체 디버그 온/오프
    [Min(1)] public int logEveryNFrames = 10; // N프레임마다 1번 출력
    public bool logActuatorDetails = false;   // 모터별 상세 로그

    // -------- 내부 상태 --------
    private float refMaxAmpBody = 1f;
    private float refMaxAmpHand = 1f;     // 정규화 기준
    private float stepDt = 0.1f;      // 실제 전송 간격
    private float totalPlayable = 0f; // 전체 재생 가능 길이
    private float accum = 0f;
    private float tCursor = 0f;       // 현재 재생 시간
    private bool  isPlaying = false;
    private float remaining = 0f;
    private float hitGain = 1f;       // 히트 세기에 따른 가산 게인

    void OnEnable()
    {
        if (piezo) piezo.OnHit += HandleHit;
    }
    void OnDisable()
    {
        if (piezo) piezo.OnHit -= HandleHit;
        StopAllMotors();
    }

    void Start()
    {
        if (!bell)
        {
            Debug.LogError("[HapticRenderer] BellBeatRenderer가 연결되지 않았습니다.");
            enabled = false; return;
        }
        if (actuators == null || actuators.Length == 0)
        {
            Debug.LogError("[HapticRenderer] Actuators 배열이 비어 있습니다.");
            enabled = false; return;
        }

        CheckActuatorConfig();
        bell.PrecomputeIfNeeded();

        // 전송 간격/총 길이 결정
        stepDt = (intervalTime > 0f) ? intervalTime : Mathf.Max(0.1f, bell.intervalTime);
        totalPlayable = (playLength > 0f) ? playLength : Mathf.Max(0.1f, bell.preCalculateSeconds);

        // 참조 최대 진폭 샘플링(시간 x 액추에이터 위치)
        refMaxAmpBody = SampleReferenceMaxAmplitudeBody();
        refMaxAmpHand = SampleReferenceMaxAmplitudeHand();
        if (refMaxAmpBody <= 0f) refMaxAmpBody = 1f;
        if (refMaxAmpHand <= 0f) refMaxAmpHand = 1f;

        accum = 0f;
        tCursor = 0f;
        isPlaying = false;
        remaining = 0f;
        hitGain = 1f;

        Debug.Log("[HapticRenderer] refMaxAmpBody=" + refMaxAmpBody.ToString("F4") +
                  ", refMaxAmpHand=" + refMaxAmpHand.ToString("F4"));

        if (playOnAwake) StartCoroutine(CoPlayOnAwake());
    }

    void CheckActuatorConfig()
    {
        if (actuators == null || actuators.Length == 0)
        {
            Debug.LogError("[HapticRenderer] Actuators 배열이 비어있습니다!");
            return;
        }

        int vestCount = 0;
        int gloveLCount = 0;
        int gloveRCount = 0;

        foreach (var act in actuators)
        {
            if (act.targetDevice == HapticDeviceType.Vest) vestCount++;
            else if (act.targetDevice == HapticDeviceType.GloveLeft) gloveLCount++;
            else if (act.targetDevice == HapticDeviceType.GloveRight) gloveRCount++;
        }

        Debug.Log($"[HapticRenderer] Configured Actuators: Vest={vestCount}, GloveL={gloveLCount}, GloveR={gloveRCount}");

        if (gloveLCount == 0 && gloveRCount == 0)
        {
            Debug.LogWarning("[HapticRenderer] 장갑(Glove) 액추에이터가 설정되지 않았습니다. Inspector에서 'Actuators' 리스트를 확인하고 'Target Device'를 GloveLeft/Right로 변경하세요.");
        }
    }

    System.Collections.IEnumerator CoPlayOnAwake()
    {
        if (playOnAwakeDelay > 0f)
            yield return new WaitForSeconds(playOnAwakeDelay);

        if (useHitStrength)
        {
            // 기존 HandleHit 로직 재사용 (재시작/남은 시간 세팅 포함)
            HandleHit(playOnAwakeStrength);
        }
        else
        {
            // 고정 게인으로 바로 시작
            hitGain = Mathf.Max(0f, playOnAwakeFixedGain);
            tCursor = 0f;
            accum   = 0f;
            remaining = totalPlayable;
            isPlaying = true;

            // 첫 패킷을 바로 쏘고 시작하면 초반 공백이 줄어듭니다(선택)
            if (frameDrivenSend)
                SendFrame(0f, Mathf.Max(minPacketMs, 100));
        }
    }

    // PiezoReader에서 이벤트 들어오는 지점
    void HandleHit(int strength)
    {
        if (useHitStrength)
        {
            float t = Mathf.InverseLerp(piezo.hitThreshold, Mathf.Max(piezo.hitThreshold + 1, maxStrength), strength);
            hitGain = Mathf.Max(minHitGain, t) * strengthScale;
        }
        else
        {
            hitGain = fixedHitGain;  // 세기 무시하고 항상 고정값
        }
        
        refMaxAmpBody = SampleReferenceMaxAmplitudeBody();
        refMaxAmpHand = SampleReferenceMaxAmplitudeHand();
        if (refMaxAmpBody <= 0f) refMaxAmpBody = 1f;
        if (refMaxAmpHand <= 0f) refMaxAmpHand = 1f;

        if (restartOnNewHit || !isPlaying)
        {
            tCursor = 0f;
            accum = 0f;
        }
        remaining = totalPlayable;
        isPlaying = true;
    }

    void Update()
    {
        if (!isPlaying) return;

        if (frameDrivenSend)
        {
            // 프레임 기반 전송: 끊김 방지
            float dt = Time.deltaTime;
            float dtLeft = dt;

            // 재생 타임라인 업데이트
            tCursor += dt;
            remaining -= dt;
            if (remaining <= 0f)
            {
                isPlaying = false;
                StopAllMotors();
                return;
            }

            // 한 프레임이 너무 길면 여러 청크로 쪼개 연속 호출
            int maxChunk = Mathf.Max(minPacketMs, maxChunkMs);
            while (dtLeft > 0f)
            {
                // 이번 청크 지속시간(초)
                float chunkSec = Mathf.Min(dtLeft, maxChunk / 1000f);

                // bHaptics에 줄 패킷 지속시간(최소 minPacketMs 보장)
                int durationMs = Mathf.Max(minPacketMs, Mathf.RoundToInt(chunkSec * 1000f));

                // 시간 샘플은 현재 tCursor 기준(원하면 중앙값 샘플링: tCursor - dtLeft + chunkSec*0.5f)
                float sampleT = tCursor - dtLeft + chunkSec * 0.5f;

                SendFrame(sampleT, durationMs);
                dtLeft -= chunkSec;
            }
        }
        else
        {
            // 기존: 고정 스텝 방식
            accum += Time.deltaTime;
            while (accum >= stepDt && isPlaying)
            {
                accum   -= stepDt;
                tCursor += stepDt;
                remaining -= stepDt;

                if (remaining <= 0f)
                {
                    isPlaying = false;
                    StopAllMotors();
                    break;
                }

                int durationMs = Mathf.Max(minPacketMs, Mathf.RoundToInt(stepDt * 1000f));
                SendFrame(tCursor, durationMs);
            }
        }
    }

    void SendFrame(float t, int durationMs)
    {
        // 모터 강도 버퍼(0..100)
        int[] vestConfig  = new int[vestMotorCount];
        int[] gloveLConfig = new int[gloveMotorCount];
        int[] gloveRConfig = new int[gloveMotorCount];

        bool isContact = (positionFollower != null) && positionFollower.isContacted;
        Vector3 impactPos = (strikerImpactPoint != null) ? strikerImpactPoint.position : bell.BellCenter;

        // ========== 빌드 단계 ==========
        for (int a = 0; a < actuators.Length; a++)
        {
            var act = actuators[a];
            if (act == null) continue;

            // 최대 인덱스 보호
            int maxIdx = (act.targetDevice == HapticDeviceType.Vest) ? vestMotorCount : gloveMotorCount;
            if (act.index >= maxIdx) continue; // 범위 벗어나면 스킵

            int idx = Mathf.Clamp(act.index, 0, maxIdx - 1);
            Vector3 listenerPos = act.transform ? act.transform.position : bell.BellCenter;
            float refAmp = act.isHand ? refMaxAmpHand : refMaxAmpBody;
            float level;

            // [물리 모델 계산]
            if (act.isHand && isContact)
            {
                // 손 + 접촉 (고체 전파)
                float sourceVibration = bell.EvaluateHaptic01(impactPos, t, refMaxAmpHand, false); 
                float transmission = bell.GetStrikerTransmission(impactPos, listenerPos, t);
                level = sourceVibration * transmission * hitGain * gain * act.localGain;
                // Debug.Log($"[HapticRenderer] Hand Contact - Actuator {a} | SourceVib: {sourceVibration:F4}, Transmission: {transmission:F4}, Level: {level:F4}");
            }
            else
            {
                // 몸통 or 손 비접촉 (공기 전파)
                float airVibration = bell.EvaluateHaptic01(listenerPos, t, refAmp, useDirectivity);
                level = airVibration * gain * act.localGain * hitGain;
                if (act.isHand) level *= handAirMultiplier;
                // Debug.Log($"[HapticRenderer] Air Vibration - Actuator {a} | AirVib: {airVibration:F4}, Level: {level:F4}");
            }

            // [후처리]
            if (subtractFloor01 > 0f)
            {
                if (renormalizeAfterCut)
                {
                    float denom = Mathf.Max(1e-6f, 1f - subtractFloor01);
                    level = Mathf.Clamp01((level - subtractFloor01) / denom);
                }
                else
                {
                    level = Mathf.Max(0f, level - subtractFloor01);
                }
            }

            if (level < silenceBelow) level = 0f;
            if (level > levelCap)     level = levelCap;

            int intensity = Mathf.RoundToInt(Mathf.Clamp01(level) * 100f);

            // [할당]
            switch (act.targetDevice)
            {
                case HapticDeviceType.Vest:
                    if (intensity > vestConfig[idx]) vestConfig[idx] = intensity;
                    break;
                case HapticDeviceType.GloveLeft:
                    if (intensity > gloveLConfig[idx]) gloveLConfig[idx] = intensity;
                    break;
                case HapticDeviceType.GloveRight:
                    if (intensity > gloveRConfig[idx]) gloveRConfig[idx] = intensity;
                    break;
            }
        }

        // ========== 디버그 출력 ==========
        if (debugLogs && (Time.frameCount % Mathf.Max(1, logEveryNFrames) == 0))
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            sb.Append($"[HapticRenderer] t={t:F2}s ({durationMs}ms) | ");

            // 배열 분석해서 요약 문자열 만들기
            void AppendDeviceSummary(string name, int[] cfg)
            {
                int activeCount = 0;
                int maxVal = 0;
                for (int i = 0; i < cfg.Length; i++)
                {
                    if (cfg[i] > 0) activeCount++;
                    if (cfg[i] > maxVal) maxVal = cfg[i];
                }
                sb.Append($"{name}: {activeCount}on (max {maxVal}) | ");
            }

            AppendDeviceSummary("Vest", vestConfig);
            AppendDeviceSummary("GL", gloveLConfig);
            AppendDeviceSummary("GR", gloveRConfig);

            Debug.Log(sb.ToString());

            // 상세(모터별) 출력
            if (logActuatorDetails) // 변수 선언 필요 (public bool logActuatorDetails = false;)
            {
                System.Text.StringBuilder detailSb = new System.Text.StringBuilder();
                
                // 로컬 함수: 0이 아닌 값만 문자열로 반환
                string GetActiveStr(string devName, int[] cfg)
                {
                    string s = "";
                    for (int i = 0; i < cfg.Length; i++)
                    {
                        if (cfg[i] > 0) s += $"{i}:{cfg[i]} ";
                    }
                    if (s.Length > 0) return $"[{devName}] {s} ";
                    return "";
                }

                detailSb.Append(GetActiveStr("Vest", vestConfig));
                detailSb.Append(GetActiveStr("GL", gloveLConfig));
                detailSb.Append(GetActiveStr("GR", gloveRConfig));

                if (detailSb.Length > 0)
                    Debug.Log("[HapticDetails] " + detailSb.ToString());
            }
        }

        // ========== 전송 ==========
        // Vest
        if (vestMotorCount > 0)
            BhapticsLibrary.PlayMotors((int)PositionType.Vest, vestConfig, durationMs);
        
        // Glove Left
        if (gloveMotorCount > 0)
            BhapticsLibrary.PlayMotors((int)PositionType.GloveL, gloveLConfig, durationMs);

        // Glove Right
        if (gloveMotorCount > 0)
            BhapticsLibrary.PlayMotors((int)PositionType.GloveR, gloveRConfig, durationMs);
    }

    private float SampleReferenceMaxAmplitudeBody()
    {
        bell.PrecomputeIfNeeded();

        // Vest은 standard listener를 여전히 벨 주변에서 측정
        float angleDeg = (bell.sourceAnglesDeg != null && bell.sourceAnglesDeg.Length > 0)
                        ? bell.sourceAnglesDeg[0] : 0f;
        float rad = angleDeg * Mathf.Deg2Rad;

        Vector3 outward =
            new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));

        Vector3 standardListener =
            bell.BellCenter + outward * (bell.bellRadius + bell.minDistance);

        float maxAmp = 0f;
        int steps = bell.Steps;
        for (int k = 0; k < steps; k++)
        {
            float t = bell.StepToTime(k);
            float amp = bell.GetAmplitudeAt(standardListener, t, true); // directivity ON
            if (amp > maxAmp) maxAmp = amp;
        }

        return Mathf.Max(maxAmp, 1e-6f);
    }

    private float SampleReferenceMaxAmplitudeHand()
    {
        bell.PrecomputeIfNeeded();

        // 손은 "impactPos 바로 근처"에서 directivity=OFF 로 측정
        Vector3 nearSource = strikerImpactPoint
                            ? strikerImpactPoint.position
                            : bell.BellCenter;

        float maxAmp = 0f;
        int steps = bell.Steps;
        for (int k = 0; k < steps; k++)
        {
            float t = bell.StepToTime(k);
            float amp = bell.GetAmplitudeAt(nearSource, t, false);  // directivity OFF
            if (amp > maxAmp) maxAmp = amp;
        }

        return Mathf.Max(maxAmp, 1e-6f);
    }

    public void StopAllMotors()
    {
        BhapticsLibrary.StopAll();
    }
}
