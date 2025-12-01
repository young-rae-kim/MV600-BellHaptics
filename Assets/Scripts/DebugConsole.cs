using UnityEngine;
using TMPro;

public class DebugConsole : MonoBehaviour
{
    public TMP_Text consoleText;
    private static DebugConsole instance;

    private int logCount = 0;   // ★ 로그 개수 카운트

    void Awake()
    {
        instance = this;
        Application.logMessageReceived += HandleLog;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        // 10개 넘으면 전체 초기화
        if (logCount >= 5)
        {
            consoleText.text = "";
            logCount = 0;
        }

        string color = type == LogType.Error ? "red" :
                       type == LogType.Warning ? "yellow" : "white";

        consoleText.text += $"\n<color={color}>{logString}</color>";
        logCount++;   // 1개 추가
    }

    public static void Log(string msg)
    {
        Debug.Log(msg);
    }
}
