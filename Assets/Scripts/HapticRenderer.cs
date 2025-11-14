using System;
using Bhaptics.SDK2;
using UnityEngine;

public class HapticRenderer : MonoBehaviour
{
    [Serializable]
    public class Actuator
    {
        [Tooltip("bHaptics 모터 인덱스 (0 ~ motorCount-1)")]
        public int index;
        [Tooltip("이 모터의 월드 위치(벨 대비 리스너 위치로 사용)")]
        public Transform transform;
        [Tooltip("개별 게인(기본 1)")]
        public float localGain = 1f;
    }

    [Header("Input")]
    public PiezoReader piezo;                 // ← PiezoReader Drag&Drop

    [Header("Bell / Actuators")]
    public BellBeatRenderer bell;
    [Tooltip("사용할 모터들을 인덱스와 함께 등록 (예: 0~39)")]
    public Actuator[] actuators;

    [Header("bHaptics")]
    public PositionType deviceType = PositionType.Vest; // 장치 타입
    public int motorCount = 40;                         // Vest=32 등 장치 스펙에 맞게

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
    public float fixedHitGain   = 1.0f;   
    public int maxStrength = 700;
    public float strengthScale = 1.0f;
    public float minHitGain = 0.2f;

    [Header("Bell evaluation")]
    [Tooltip("벨 방향성 감쇠 사용 여부 (BellBeatRenderer.GetAmplitudeAt의 인자)")]
    public bool useDirectivity = true;

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
    private float refMaxAmp = 1f;     // 정규화 기준 (Start에서 샘플링)
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
            Debug.LogError("[HapticRenderer] Bell이 지정되지 않았습니다.");
            enabled = false; return;
        }
        if (actuators == null || actuators.Length == 0)
        {
            Debug.LogError("[HapticRenderer] Actuators 배열이 비어 있습니다.");
            enabled = false; return;
        }

        bell.PrecomputeIfNeeded();

        // 전송 간격/총 길이 결정
        stepDt = (intervalTime > 0f) ? intervalTime : Mathf.Max(0.1f, bell.intervalTime);
        totalPlayable = (playLength > 0f) ? playLength : Mathf.Max(0.1f, bell.preCalculateSeconds);

        // 참조 최대 진폭 샘플링(시간 x 액추에이터 위치)
        refMaxAmp = SampleReferenceMaxAmplitude();
        if (refMaxAmp <= 0f) refMaxAmp = 1f;

        accum = 0f;
        tCursor = 0f;
        isPlaying = false;
        remaining = 0f;
        hitGain = 1f;

        if (playOnAwake) StartCoroutine(CoPlayOnAwake());
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
            hitGain = fixedHitGain;  // ← 세기 무시하고 항상 고정값
        }
        
        refMaxAmp = SampleReferenceMaxAmplitude();
        if (refMaxAmp <= 0f) refMaxAmp = 1f;

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
        int[] config = new int[motorCount];

        // ========== 빌드 단계 ==========
        for (int a = 0; a < actuators.Length; a++)
        {
            var act = actuators[a];
            if (act == null) continue;

            int idx = Mathf.Clamp(act.index, 0, motorCount - 1);
            Vector3 listenerPos = act.transform ? act.transform.position : bell.BellCenter;

            // 0..1 스케일(벨 내부 맵핑) → 추가 게인/세기 반영
            float base01 = bell.EvaluateHaptic01(listenerPos, t, refMaxAmp, useDirectivity);
            float level = base01 * gain * act.localGain * hitGain;
            
            // ▼ 바닥 감산 적용(순서: 감산/재정규화 → silenceBelow/levelCap)
            if (subtractFloor01 > 0f)
            {
                if (renormalizeAfterCut)
                {
                    // Crop+Scale: 남은 범위를 다시 0..1로 펴기
                    float denom = Mathf.Max(1e-6f, 1f - subtractFloor01);
                    level = Mathf.Clamp01((level - subtractFloor01) / denom);
                }
                else
                {
                    // Cut: 그냥 깎기
                    level = Mathf.Max(0f, level - subtractFloor01);
                }
            }

            if (level < silenceBelow) level = 0f;
            if (level > levelCap)     level = levelCap;

            int intensity01_100 = Mathf.RoundToInt(Mathf.Clamp01(level) * 100f);
            if (intensity01_100 > config[idx]) config[idx] = intensity01_100; // 같은 인덱스 중 최대값
        }

        // ========== 디버그 출력 ==========
        if (debugLogs && (Time.frameCount % Mathf.Max(1, logEveryNFrames) == 0))
        {
            // 요약 정보
            int nonZero = 0, maxVal = 0, maxIdx = -1;
            for (int i = 0; i < config.Length; i++)
            {
                int v = config[i];
                if (v > 0) nonZero++;
                if (v > maxVal) { maxVal = v; maxIdx = i; }
            }

            Debug.Log(
                $"[HapticRenderer] t={t:F2}s, sendStep={stepDt:F2}s, duration={durationMs}ms | " +
                $"hitGain={hitGain:F2}, gain={gain:F2} | activeMotors={nonZero}/{motorCount}, " +
                $"max={maxVal} (idx {maxIdx})"
            );

            // 상세(모터별) — 많아질 수 있음
            if (logActuatorDetails && nonZero > 0)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
                sb.Append("[HapticRenderer] config: ");
                for (int i = 0; i < config.Length; i++)
                {
                    int v = config[i];
                    if (v > 0)
                    {
                        sb.Append(i).Append('=').Append(v).Append(' ');
                    }
                }
                Debug.Log(sb.ToString());
            }
        }

        // ========== 전송 ==========
        BhapticsLibrary.PlayMotors((int)deviceType, config, durationMs);
    }

    float SampleReferenceMaxAmplitude()
    {
        bell.PrecomputeIfNeeded();

        // 1) 표준 리스너 포인트: 벨 중심에서 한 소스 방향(예: 0°)으로
        //    "소스 반경 + minDistance"만큼 떨어진 위치를 사용
        float angleDeg = (bell.sourceAnglesDeg != null && bell.sourceAnglesDeg.Length > 0)
                        ? bell.sourceAnglesDeg[0] : 0f;
        float rad = angleDeg * Mathf.Deg2Rad;

        // 소스가 있는 수평 원(반지름 = bellRadius)에서 바깥쪽으로 minDistance 만큼 더 떨어진 지점
        Vector3 outward = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        Vector3 standardListener = bell.BellCenter + outward * (bell.bellRadius + bell.minDistance);

        // 2) 시간 전체에서의 최대 진폭을 검색 (방향성 적용 on)
        int steps = bell.Steps;
        if (steps <= 0) return 1f;

        float maxAmp = 0f;
        for (int k = 0; k < steps; k++)
        {
            float t = bell.StepToTime(k);
            float amp = bell.GetAmplitudeAt(standardListener, t, /*useDirectivity:*/ true);
            if (amp > maxAmp) maxAmp = amp;
        }

        return Mathf.Max(maxAmp, 1e-6f);
    }

    public void StopAllMotors()
    {
        BhapticsLibrary.StopAll();
    }
}
