using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class HitSender : MonoBehaviour
{
    [Header("Refs")]
    public PiezoReader piezo;  

    [Header("Flask")]
    public string flaskBaseUrl = "http://192.168.0.22:5000";

    void OnEnable()
    {
        if (piezo != null)
            piezo.OnHit += HandleHit;
    }

    void OnDisable()
    {
        if (piezo != null)
            piezo.OnHit -= HandleHit;
    }

    void HandleHit(int strength)
    {
        // Piezo HIT 발생 = Pressed 같은 상태
        Debug.Log($"[PiezoToFlaskSender] HIT -> send Pressed, strength={strength}");
        StartCoroutine(SendPressed());
    }

    IEnumerator SendPressed()
    {
        string url = flaskBaseUrl + "/press_button";

        // 바디 없이도 상관 없지만, 형식 맞춰 간단한 JSON 전송
        var json = "{\"source\":\"piezo\"}";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning("[PiezoToFlaskSender] POST failed: " + req.error);
        }
        else
        {
            Debug.Log("[PiezoToFlaskSender] POST /press_button ok: " + req.downloadHandler.text);
        }
    }
}
