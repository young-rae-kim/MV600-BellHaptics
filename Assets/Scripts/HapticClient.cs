using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class HapticClient : MonoBehaviour
{
    public string serverURL = "ws://192.168.0.22:5000/ws";
    public HapticRenderer hapticRenderer;   // 🔹 Inspector에 HapticRenderer 넣기

    private ClientWebSocket ws;
    private CancellationTokenSource cts;

    private bool hapticPending = false;     // 메인 스레드에서 처리할 플래그

    void Start()
    {
        ConnectWebSocket();
        // 서버가 응답해주도록 0.1초마다 heartbeat 보낼 수도 있음
        InvokeRepeating(nameof(SendHeartbeat), 0.1f, 0.1f);
    }

    async void ConnectWebSocket()
    {
        try
        {
            ws = new ClientWebSocket();
            cts = new CancellationTokenSource();

            await ws.ConnectAsync(new Uri(serverURL), cts.Token);
            Debug.Log("[PcHapticWsClient] Connected to " + serverURL);

            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception e)
        {
            Debug.LogError("[PcHapticWsClient] WebSocket Error: " + e.Message);
        }
    }

    async Task ReceiveLoop()
    {
        byte[] buffer = new byte[1024];

        while (ws != null && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogError("[PcHapticWsClient] ReceiveLoop error: " + e.Message);
                break;
            }

            if (result.Count > 0)
            {
                string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Debug.Log("[PcHapticWsClient] RECV: " + msg);

                // 매우 단순 파싱: "haptic": true 있으면 처리
                if (msg.Contains("\"haptic\"") && msg.Contains("true"))
                {
                    hapticPending = true;
                }
            }
        }
    }

    async void SendHeartbeat()
    {
        if (ws == null || ws.State != WebSocketState.Open)
            return;

        // 서버 쪽 websocket 루프가 돌도록 아주 작은 패킷 보내주기
        var payload = "{\"user_id\": \"PC\", \"is_contact\": false}";
        try
        {
            await ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
                WebSocketMessageType.Text,
                true,
                cts.Token
            );
        }
        catch (Exception e)
        {
            Debug.LogError("[PcHapticWsClient] SendHeartbeat error: " + e.Message);
        }
    }

    void Update()
    {
        // 메인 스레드에서 HapticRenderer 호출
        if (hapticPending)
        {
            hapticPending = false;

            if (hapticRenderer != null)
            {
                hapticRenderer.TriggerFromNetwork(400); // strength는 알아서 튜닝
            }
            else
            {
                Debug.LogWarning("[PcHapticWsClient] hapticRenderer not assigned.");
            }
        }
    }

    void OnDestroy()
    {
        if (ws != null)
        {
            ws.Dispose();
            ws = null;
        }
        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
