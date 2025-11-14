using System;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

public class PiezoReader : MonoBehaviour
{
      [Header("Serial")]
    public string portName = "COM3";   // macOS 예: "/dev/tty.usbmodem1101"
    public int baudRate = 9600;

    [Header("Detection")]
    [Tooltip("이 값 이상이면 'pushed/충격'으로 간주")]
    public int hitThreshold = 30;
    [Tooltip("중복 트리거 방지 시간(ms)")]
    public int debounceMs = 120;
    [Tooltip("상승 에지에서만 감지")]
    public bool useRisingEdge = true;

    [Header("(선택) 간단 평활")]
    [Range(1, 10)] public int movingAverage = 1; // 1이면 비활성

    public event Action<int> OnHit;     // 감지 시 값 전달
    public event Action<int> OnValue;   // 매 샘플 값 전달(옵션)

    SerialPort _port;
    Thread _readThread;
    volatile bool _running;

    readonly ConcurrentQueue<Action> _mainQ = new ConcurrentQueue<Action>();
    int _lastVal = 0;
    long _lastHitTick = 0;

    int _maSum = 0, _maCount = 0;

    void Start()
    {
        try
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = 100,
                NewLine = "\n"
            };
            _port.Open();

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            Debug.Log($"[ArduinoPiezoReader] Opened {portName} @ {baudRate}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ArduinoPiezoReader] Open failed: {e.Message}");
        }
    }

    void Update()
    {
        while (_mainQ.TryDequeue(out var a)) a.Invoke();
    }

    void OnDestroy()
    {
        _running = false;
        try { if (_readThread != null && _readThread.IsAlive) _readThread.Join(300); } catch {}
        try { if (_port != null && _port.IsOpen) _port.Close(); } catch {}
    }

    void ReadLoop()
    {
        while (_running && _port != null && _port.IsOpen)
        {
            try
            {
                string line = _port.ReadLine(); // e.g., "523"
                if (int.TryParse(line.Trim(), out int val))
                {
                    int v = ApplyMA(val);   // 이동평균 적용(선택)
                    Detect(v);
                    EnqueueMain(() => OnValue?.Invoke(v));
                }
            }
            catch (TimeoutException) { /* ignore */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ArduinoPiezoReader] {ex.Message}");
                Thread.Sleep(50);
            }
        }
    }

    int ApplyMA(int val)
    {
        if (movingAverage <= 1) return val;
        _maSum += val; _maCount++;
        if (_maCount >= movingAverage)
        {
            int avg = _maSum / _maCount;
            _maSum = 0; _maCount = 0;
            return avg;
        }
        return val; // 초기 구간은 원시값
    }

    void Detect(int val)
    {
        bool crossed = val >= hitThreshold;
        bool rising = useRisingEdge ? (crossed && _lastVal < hitThreshold) : crossed;

        long now = Environment.TickCount;
        if (rising && (now - _lastHitTick) >= debounceMs)
        {
            _lastHitTick = now;
            EnqueueMain(() =>
            {
                OnHit?.Invoke(val);
                Debug.Log($"[ArduinoPiezoReader] HIT val={val}");
            });
        }
        _lastVal = val;
    }

    void EnqueueMain(Action a) => _mainQ.Enqueue(a);
}
