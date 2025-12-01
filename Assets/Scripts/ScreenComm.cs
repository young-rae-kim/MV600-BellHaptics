using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public class ScreenComm : MonoBehaviour
{
    public string serverURL = "ws://192.168.50.107:5000/ws";
    public TMP_Text currIP;
    public TMP_Text statusText;  // 연결 상태 표시용 

    private ClientWebSocket ws;
    private Uri serverUri;
    private CancellationTokenSource cts;
    private int buttonIndex = -1;

    private bool flaskToggle = true;  // 기존 isConnected → 불명확해서 이름 변경
    private bool isPaused = false;

    public void ToggleSetFlask(bool toggle)
    {
        flaskToggle = toggle;
        ConnectWebSocket();
    }

    async void ConnectWebSocket()
    {
        if (statusText != null)
        {
            statusText.text = "Connecting...";
            statusText.color = Color.yellow;
        }

        ws = new ClientWebSocket();
        cts = new CancellationTokenSource();

        try
        {
            serverUri = flaskToggle ? new Uri(serverURL) : new Uri(serverURL.Replace("/ws", ""));
            await ws.ConnectAsync(serverUri, cts.Token);

            Debug.Log("WebSocket connected to " + serverURL);

            if (statusText != null)
            {
                statusText.text = "Connected";
                statusText.color = Color.green;
            }

            _ = Task.Run(ReceiveLoop);
        }
        catch (Exception ex)
        {
            Debug.LogError("WebSocket connection failed: " + ex.Message);

            if (statusText != null)
            {
                statusText.text = "Connection Failed";
                statusText.color = Color.red;
            }
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                if (isPaused)
                {
                    await Task.Delay(100);
                    continue;
                }

                WebSocketReceiveResult result =
                    await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Debug.Log("Server closed connection.");
                    break;
                }

                string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Debug.Log("Server says: " + msg);

                if (msg.Contains("Received button index:"))
                {
                    buttonIndex = int.Parse(msg.Split(':')[1].Trim());
                }
                else
                {
                    buttonIndex = -1;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("ReceiveLoop error: " + ex.Message);
        }

        if (statusText != null)
        {
            statusText.text = "Disconnected";
            statusText.color = Color.red;
        }
    }

    public int GetButtonIndex()
    {
        return buttonIndex;
    }

    public async void SendCoordinates(Vector2 coords, int tag)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;

        string json = "{\"x\":" + coords.x + ", \"y\":" + coords.y + ", \"tag\":" + tag + "}";
        ArraySegment<byte> bytesToSend = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));

        await ws.SendAsync(bytesToSend, WebSocketMessageType.Text, true, cts.Token);
        Debug.Log("Sent: " + json);
    }

    void Start()
    {
        PlayerPrefs.SetString("serverURL", serverURL);
        PlayerPrefs.Save();

        if (PlayerPrefs.HasKey("serverURL"))
        {
            serverURL = PlayerPrefs.GetString("serverURL", serverURL);
            currIP.text = "Current IP: " + serverURL.Replace("ws://", "").Replace(":5000/ws", "");
            Debug.Log("Loaded server URL: " + serverURL);
        }
        else
        {
            currIP.text = "Current IP: localhost";
        }

        ConnectWebSocket();
    }

    public void SetServerURL(TMP_InputField field)
    {
        serverURL = "ws://" + field.text + ":5000/ws";
        currIP.text = "Current IP: " + field.text;

        PlayerPrefs.SetString("serverURL", serverURL);
        PlayerPrefs.Save();

        ConnectWebSocket();
    }

    private void OnApplicationPause(bool pause)
    {
        isPaused = pause;
    }

    private void OnApplicationFocus(bool focus)
    {
        isPaused = !focus;
    }

    private async void OnApplicationQuit()
    {
        if (ws != null)
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            ws.Dispose();
        }
    }
}
