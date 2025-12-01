using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public class BellComm : MonoBehaviour
{
    public string serverURL = "ws://192.168.0.22:5000/ws";

    public TMP_Text statusText;
    public TMP_Text messageText;
    public Transform hmd;

    private ClientWebSocket ws;
    private CancellationTokenSource cts;

    // 상태 플래그
    private bool pressedPending = false;       // 새 Pressed 이벤트가 들어왔는지
    private string lastWsMessage = "";         // 디버그용

    // Pressed 표시 시간
    public float pressedDisplayDuration = 0.1f;  // Pressed! 를 표시할 시간(초)
    private float pressedTimer = 0f;             // 남은 시간
    private string defaultMessage = "";          // 기본 텍스트(처음 상태 저장)

    void Start()
    {
        if (hmd == null)
            hmd = Camera.main.transform;

        if (messageText != null)
            defaultMessage = messageText.text;   // 시작할 때 기본 텍스트 저장

        ConnectWebSocket();
        InvokeRepeating(nameof(SendHMDPosition), 0.1f, 0.1f);
    }

    async void ConnectWebSocket()
    {
        try
        {
            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();

            if (statusText != null)
            {
                statusText.text = "Connecting...";
                statusText.color = Color.yellow;
            }

            await ws.ConnectAsync(new Uri(serverURL), cts.Token);

            if (statusText != null)
            {
                statusText.text = "Connected";
                statusText.color = Color.green;
            }

            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception e)
        {
            if (statusText != null)
            {
                statusText.text = "Connection Failed";
                statusText.color = Color.red;
            }
            Debug.LogError("WebSocket Error: " + e.Message);
        }
    }

    async Task ReceiveLoop()
    {
        byte[] buffer = new byte[1024];

        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogError("ReceiveLoop error: " + e.Message);
                break;
            }

            if (result.Count > 0)
            {
                string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                lastWsMessage = msg;
                Debug.Log("[WS RECV] " + msg);

                if (msg.Contains("Pressed"))
                {
                    pressedPending = true;
                }
            }
        }
    }

    async void SendHMDPosition()
    {
        if (ws == null || ws.State != WebSocketState.Open)
            return;

        Vector3 pos = hmd.position;
        string json = $"{{\"hmd_x\":{pos.x}, \"hmd_y\":{pos.y}, \"hmd_z\":{pos.z}}}";

        Debug.Log("[WS SEND] " + json);

        await ws.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
            WebSocketMessageType.Text,
            true,
            cts.Token
        );
    }

    void Update()
    {
        // 새로운 Pressed 이벤트가 들어왔을 때
        if (pressedPending)
        {
            pressedPending = false;              // 한 번만 처리
            pressedTimer = pressedDisplayDuration;  // 타이머 리셋

            if (messageText != null)
                messageText.text = "Pressed!";
        }

        // Pressed 표시 중이면 타이머 감소시키다가 끝나면 원래 텍스트로 복귀
        if (pressedTimer > 0f)
        {
            pressedTimer -= Time.deltaTime;

            if (pressedTimer <= 0f)
            {
                pressedTimer = 0f;
                if (messageText != null)
                    messageText.text = defaultMessage;  // 다시 원래 상태로
            }
        }

        // lastWsMessage를 DebugConsole 등에 띄우고 싶으면 여기서 사용 가능
    }
}
