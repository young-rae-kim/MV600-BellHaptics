using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

[System.Serializable]
public class HmdPosDto
{
    public float x;
    public float y;
    public float z;
}

public class PositionFollower : MonoBehaviour
{
    [Header("Target")]
    public Transform humanDummy;   // HumanDummy 루트 Transform

    [Header("Flask")]
    public string flaskBaseUrl = "http://192.168.0.22:5000";
    public float pollInterval = 0.05f;  // 20Hz

    void Start()
    {
        if (humanDummy == null)
        {
            humanDummy = this.transform;
        }
        StartCoroutine(PollHmdLoop());
    }

    IEnumerator PollHmdLoop()
    {
        string url = flaskBaseUrl + "/get_hmd_position";

        while (true)
        {
            UnityWebRequest req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var json = req.downloadHandler.text;
                    // {"x":..., "y":..., "z":...}
                    HmdPosDto dto = JsonUtility.FromJson<HmdPosDto>(json);

                    // 월드 좌표를 그대로 HumanDummy 위치로 사용
                    humanDummy.position = new Vector3(dto.x, 0, dto.z);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[HumanDummyFollower] parse error: " + e.Message);
                }
            }
            else
            {
                Debug.LogWarning("[HumanDummyFollower] GET failed: " + req.error);
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }
}
