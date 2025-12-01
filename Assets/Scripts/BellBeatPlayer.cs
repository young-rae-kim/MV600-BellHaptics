using System.Collections;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class BellBeatPlayer : MonoBehaviour
{
    [Header("Refs")]
    public PiezoReader piezo;           // ← HIT 이벤트 소스
    public BellBeatRenderer bell;       // 길이 기본값에 사용(없어도 동작)

    [Header("Playback")]
    [Tooltip("HIT 후 재생할 길이(초). 0 이면 bell.preCalculateSeconds 사용")]
    public float playLength = 0f;
    [Tooltip("히트가 새로 오면 기존 재생을 끊고 처음부터 재시작")]
    public bool restartOnNewHit = true;
    [Tooltip("AudioSource를 자동으로 Play/Stop 관리")]
    public bool controlAudioSource = true;
    [Tooltip("clip이 playLength보다 짧을 때 반복 재생할지 여부")]
    public bool loopIfShort = true;

    [Header("Volume / Envelope")]
    [Range(0f, 1f)] public float baseVolume = 1.0f;
    [Tooltip("끝날 때 페이드아웃 시간(초)")]
    [Range(0f, 1f)] public float fadeOutTime = 0.2f;

    [Header("(옵션) 히트 세기 반영")]
    public bool useHitStrength = false;
    public int  maxStrength = 700;
    public float strengthScale = 1.0f;
    public float minHitGain   = 0.2f;

    [Header("Debug")]
    public bool debugLogs = false;

    // ---- 내부 상태 ----
    AudioSource _src;
    Coroutine _playCo;
    bool _isPlaying = false;
    float _totalPlayable = 0f;   // 실제 재생할 길이
    float _hitGain = 1f;

    void Awake()
    {
        _src = GetComponent<AudioSource>();
        if (!_src) _src = gameObject.AddComponent<AudioSource>();
    }

    void OnEnable()
    {
        if (piezo) piezo.OnHit += HandleHit;
    }

    void OnDisable()
    {
        if (piezo) piezo.OnHit -= HandleHit;
        StopPlayingImmediate();
    }

    void Start()
    {
        // 총 길이 결정
        float bellLen = (bell ? Mathf.Max(0.1f, bell.preCalculateSeconds) : 0.0f);
        _totalPlayable = (playLength > 0f) ? playLength : (bellLen > 0f ? bellLen : (_src.clip ? _src.clip.length : 1.0f));

        // 사운드 루프는 우리가 수동 관리 (필요시만 루프)
        if (_src) _src.loop = false;
    }

    void HandleHit(int strength)
    {
        // 히트 세기 → 게인(선택)
        if (useHitStrength)
        {
            float t = 0f;
            if (maxStrength <= piezo.hitThreshold) maxStrength = piezo.hitThreshold + 1;
            t = Mathf.InverseLerp(piezo.hitThreshold, maxStrength, strength);
            _hitGain = Mathf.Max(minHitGain, t) * strengthScale;
        }
        else
        {
            _hitGain = 1f;
        }

        if (restartOnNewHit && _isPlaying)
            StopPlayingImmediate(); // 즉시 끊기

        // 재생 시작
        if (_playCo != null) StopCoroutine(_playCo);
        _playCo = StartCoroutine(PlayForDuration(_totalPlayable));

        if (debugLogs)
            Debug.Log($"[BellBeatPlayer] HIT: strength={strength}, hitGain={_hitGain:F2}, duration={_totalPlayable:F2}s");
    }

    IEnumerator PlayForDuration(float duration)
    {
        _isPlaying = true;

        if (_src && controlAudioSource)
        {
            // 길이 대비 loop 필요 시 켜기
            if (loopIfShort && _src.clip && _src.clip.length < duration)
                _src.loop = true;
            else
                _src.loop = false;

            _src.volume = Mathf.Clamp01(baseVolume * _hitGain);
            _src.time = 0f; // 처음부터
            _src.Play();
        }

        float t = 0f;
        float fadeStart = Mathf.Max(0f, duration - fadeOutTime);
        float startVol = Mathf.Clamp01(baseVolume * _hitGain);
        float endVol = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;

            // 페이드아웃 구간이면 선형 페이드
            if (_src && controlAudioSource && fadeOutTime > 0f && t >= fadeStart)
            {
                float u = Mathf.InverseLerp(fadeStart, duration, t); // 0..1
                _src.volume = Mathf.Lerp(startVol, endVol, u);
            }

            yield return null;
        }

        // 종료 처리
        if (_src && controlAudioSource)
        {
            _src.volume = 0f;
            _src.Stop();
            _src.loop = false;
        }

        _isPlaying = false;
        _playCo = null;
    }

    void StopPlayingImmediate()
    {
        if (_playCo != null)
        {
            StopCoroutine(_playCo);
            _playCo = null;
        }
        if (_src && controlAudioSource)
        {
            _src.volume = 0f;
            _src.Stop();
            _src.loop = false;
        }
        _isPlaying = false;
    }

    // 외부에서 길이 변경 시 호출(선택)
    public void SetPlayLength(float seconds)
    {
        _totalPlayable = Mathf.Max(0.05f, seconds);
        if (debugLogs)
            Debug.Log($"[BellBeatPlayer] playLength set to {_totalPlayable:F2}s");
    }
}
