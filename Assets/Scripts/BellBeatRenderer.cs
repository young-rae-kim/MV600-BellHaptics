using System;
using UnityEngine;

public class BellBeatRenderer : MonoBehaviour
{
    [Header("Bell geometry (world)")]
    [Tooltip("종의 높이 (m)")]
    public float bellHeight = 1.0f;

    [Tooltip("종의 반지름 (m)")]
    public float bellRadius = 1.1245f;

    /// <summary>종 중심 (월드 좌표)</summary>
    public Vector3 BellCenter => transform.position + Vector3.up * bellHeight;

    [Header("Debug")]
    public bool debugLog = false;

    [Tooltip("N 프레임마다 한 번씩 로그")]
    public int logEveryNFrames = 30;

    [Header("Early boom")]
    public float earlyBoost = 1.0f;   // t=0에서 200%까지 키움 (1 + earlyBoost가 초기 배수)
    public float tauImpact = 0.7f;    // 초기 빠른 감쇄 타임상수(초) — 0.5~1.0 권장
    public float boostEnd = 4.0f;     // 3초까지 부스트를 1로 서서히 페이드아웃
    public float beatDepthEarly = 0.55f; // 초기 3초 동안의 더 큰 맥놀이 대비(예: 0.9)


    [Header("Beat Parameters")]
    [Tooltip("맥놀이 주기 (초)")]
    public float beatPeriod = 2.9f;

    [Tooltip("진폭 변조 깊이 (0~1 권장)")]
    [Range(0f, 1f)]
    public float beatDepth = 0.65f;

    [Tooltip("소스 각도 (deg) - 수평 원주 상의 4개 지점")]
    public float[] sourceAnglesDeg = { 45f, 135f, 225f, 315f };

    [Tooltip("소스별 위상 (deg)")]
    public float[] phaseOffsetsDeg = { 0f, 90f, 180f, 270f };

    [Header("Attenuation Parameters")]
    [Tooltip("시간 감쇠 계수 (s^-1)")]
    public float lambda1 = 0.000130f;

    [Tooltip("시간 감쇠 계수 (s^-1)")]
    public float lambda2 = 0.000300f;

    [Header("Extra fast decay term (impact early drop)")]
    [Range(0f, 1f)] public float a = 0.35f;
    [Range(0f, 1f)] public float b = 0.35f; // c = 1 - a - b

    [Tooltip("초반 급감 시간상수(s)")]
    public float tauFast = 1f;

    [Tooltip("거리 감쇠 계수 (1/r^alpha)")]
    public float alpha = 1.2f;

    [Tooltip("최소 거리 (m) 클램프")]
    public float minDistance = 0.5f;

    [Tooltip("공기 흡수 사용 여부")]
    public bool useAirAbsorption = false;

    [Tooltip("공기 흡수 계수 (m^-1)")]
    public float airAlphaPerMeter = 0f;

    [Header("Pre-calculation")]
    [Tooltip("미리 계산할 시간 (초)")]
    public int preCalculateSeconds = 30;

    [Tooltip("시간 샘플 간격 (초)")]
    public float intervalTime = 0.1f;

    [Tooltip("최대 거리 (m) (현재 미사용)")]
    public float maxDistance = 5.0f;

    // ================= Internal State =================
    // [소스 인덱스][시간 인덱스]에 시간 성분만 저장
    private float[][] temporalBySource;
    private int steps;
    private float fBeat;
    private bool precomputed = false;

    // === Public helpers ===
    public int Steps => steps;
    public float StepToTime(int k) => Mathf.Clamp(k, 0, steps - 1) * intervalTime;
    public bool IsReady => precomputed && temporalBySource != null && steps > 0;

    // ================= Unity Hooks =================
    private void Awake()
    {
        PrecomputeIfNeeded();
    }

    private void OnEnable()
    {
        PrecomputeIfNeeded();
    }

    private void Start()
    {
        fBeat = 1f / Mathf.Max(1e-4f, beatPeriod);
        PrecomputeIfNeeded(); // Start에서도 안전 호출
    }

    // t<boostEnd 동안에는 (1 + earlyBoost * e^{-t/tauImpact})에서 1로 서서히 보간
    float EarlyBoomMul(float t)
    {
        float e = Mathf.Exp(-t / Mathf.Max(1e-4f, tauImpact));   // 빠른 감쇄
        float target = 1f + earlyBoost * e;                       // 초기엔 >1
        float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / Mathf.Max(1e-4f, boostEnd)));
        // w=0일 때 target, w=1일 때 1로 수렴
        return Mathf.Lerp(target, 1f, w);
    }

    // 맥놀이 대비를 초기엔 크게, 3초에 걸쳐 beatDepth로 내려줌
    float DepthAt(float t)
    {
        float w = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / Mathf.Max(1e-4f, boostEnd)));
        return Mathf.Lerp(beatDepthEarly, beatDepth, w);
    }

    // ================= Core =================
    /// <summary>
    /// 빠른항을 섞은 포락선 E(t) = a e^{-λ1 t} + b e^{-λ2 t} + c e^{-t/τ_fast}, c=1-a-b
    /// </summary>
    private float EnvelopeMeasuredPlusFast(float t)
    {
        float c = Mathf.Max(0f, 1f - a - b); // a + b + c = 1
        float slow1 = Mathf.Exp(-lambda1 * t);
        float slow2 = Mathf.Exp(-lambda2 * t);
        float fast = Mathf.Exp(-t / Mathf.Max(1e-4f, tauFast));
        return a * slow1 + b * slow2 + c * fast; // E(0) = 1
    }

    /// <summary>
    /// 순수 진폭 변조: M(t) = 1 + m * sin(2π f_beat t + φ)
    /// </summary>
    private float BeatAM(float t, float phiRad)
    {
        return 1f + beatDepth * Mathf.Sin(2f * Mathf.PI * fBeat * t + phiRad);
    }

    /// <summary>
    /// 1) 시간 성분(Et * M)을 미리 계산해 저장
    /// </summary>
    private void PrecomputeTemporalOnly()
    {
        fBeat = 1f / Mathf.Max(1e-4f, beatPeriod);

        steps = Mathf.Max(1, Mathf.CeilToInt(preCalculateSeconds / Mathf.Max(1e-4f, intervalTime)));
        int S = Mathf.Max(1, sourceAnglesDeg?.Length ?? 0);

        temporalBySource = new float[S][];
        for (int s = 0; s < S; s++)
            temporalBySource[s] = new float[steps];

        for (int k = 0; k < steps; k++)
        {
            float t = k * intervalTime;
            float Et = EnvelopeMeasuredPlusFast(t) * EarlyBoomMul(t);

            for (int s = 0; s < S; s++)
            {
                float phiDeg = (phaseOffsetsDeg != null && s < phaseOffsetsDeg.Length) ? phaseOffsetsDeg[s] : 0f;
                float phiRad = phiDeg * Mathf.Deg2Rad;
                float depth = DepthAt(t);
                float M = 1f + depth * Mathf.Sin(2f * Mathf.PI * fBeat * t + phiRad);
                temporalBySource[s][k] = Et * M;
            }
        }

        precomputed = true;
    }

    /// <summary>
    /// 소스의 월드 위치(벨 중심의 수평 원 둘레)
    /// </summary>
    private Vector3 GetSourcePos(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        return BellCenter + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * bellRadius;
    }

    /// <summary>
    /// 2) 사용자 위치 + 시간으로 진폭 조회 (거리/방향 감쇠 포함)
    /// </summary>
    public float GetAmplitudeAt(Vector3 listenerPos, float t, bool useDirectivity = true)
    {
        PrecomputeIfNeeded();
        if (!IsReady) return 0f;

        int k = Mathf.Clamp(Mathf.FloorToInt(t / Mathf.Max(1e-4f, intervalTime)), 0, steps - 1);
        Vector3 center = BellCenter;
        float sum = 0f;

        bool doLog = debugLog && (Time.frameCount % Mathf.Max(1, logEveryNFrames) == 0);
        if (doLog) Debug.Log($"[Bell] t={t:F3}s, timeIdx={k}, listener={listenerPos}");

        int S = temporalBySource.Length;

        for (int s = 0; s < S; s++)
        {
            if (temporalBySource[s] == null) continue;

            Vector3 sPos = GetSourcePos(sourceAnglesDeg[s]);
            Vector3 toListener = listenerPos - sPos;

            float r = Mathf.Max(minDistance, toListener.magnitude);

            // 거리 감쇠
            float g_r = Mathf.Pow(1f / r, alpha);
            if (useAirAbsorption && airAlphaPerMeter > 0f)
                g_r *= Mathf.Exp(-airAlphaPerMeter * r);

            // 방향성
            float w_dir = 1f;
            if (useDirectivity)
            {
                Vector3 outward = (sPos - center).normalized;
                Vector3 toward = toListener.normalized;
                float dot = Vector3.Dot(outward, toward);    // [-1..1]
                float w = 0.5f * (dot + 1f);                 // [0..1]
                w_dir = Mathf.Max(0.01f, w);                  // 바닥값 0.01
            }

            float temporal = temporalBySource[s][k];
            float contrib = temporal * g_r * w_dir;
            sum += contrib;

            if (doLog)
            {
                Debug.Log(
                    $" src#{s} ang={sourceAnglesDeg[s]:F1}° " +
                    $"sPos=({sPos.x:F2},{sPos.y:F2},{sPos.z:F2}) " +
                    $"r={r:F3} g_r={g_r:E3} w_dir={w_dir:F3} " +
                    $"temporal={temporal:F6} contrib={contrib:E6}"
                );
            }
        }

        if (doLog) Debug.Log($"[Bell] SUM={sum:E6}");
        return Mathf.Max(0f, sum);
    }

    // ============== Public Utility (Normalization & Mapping) ==============
    public float NormalizeAmplitude(float amp, float maxAmp)
        => (maxAmp > 0f) ? Mathf.Clamp01(amp / maxAmp) : 0f;

    public float MapToHapticsLevel(float norm)
        => Mathf.Clamp(Mathf.Min(0.8f, norm), 0f, 1f);

    /// <summary>
    /// 리스너 위치와 시간 t(초)를 넣으면 0..1의 햅틱 레벨 반환
    /// </summary>
    public float EvaluateHaptic01(Vector3 listenerPos, float t, float refMaxAmp)
    {
        float amp = GetAmplitudeAt(listenerPos, t);
        float norm = NormalizeAmplitude(amp, refMaxAmp);
        return Mathf.Clamp01(MapToHapticsLevel(norm));
    }

    public float EvaluateHaptic01(Vector3 listenerPos, float t, float refMaxAmp, bool useDirectivity)
    {
        float amp  = GetAmplitudeAt(listenerPos, t, useDirectivity); // ← 거리+방향 감쇠 반영
        float norm = NormalizeAmplitude(amp, refMaxAmp);
        return Mathf.Clamp01(MapToHapticsLevel(norm));
    }

    // ============== Safety ==============
    /// <summary>
    /// 언제 호출돼도 안전하게 준비시킴
    /// </summary>
    public void PrecomputeIfNeeded()
    {
        bool need =
            temporalBySource == null ||
            temporalBySource.Length == 0 ||
            steps <= 0 ||
            !precomputed;

        if (need)
            PrecomputeTemporalOnly();
    }
}
