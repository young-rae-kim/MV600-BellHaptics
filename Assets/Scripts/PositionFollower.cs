using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

[System.Serializable]
public class ServerVector3
{
    public float x, y, z;
    public Vector3 ToVector3() => new Vector3(x, y, z);
}

[System.Serializable]
public class ServerQuaternion
{
    public float x, y, z, w;
    public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
}

[System.Serializable]
public class ServerTransform
{
    public ServerVector3 pos;
    public ServerQuaternion rot;
}

[System.Serializable]
public class UserData
{
    // 서버의 connected_users[id] 값에 해당하는 구조
    public ServerTransform hmd;
    public bool is_contact;

    public ServerTransform[] left_hand;
    public ServerTransform[] right_hand;
}

public class PositionFollower : MonoBehaviour
{
    [Header("Configuration")]
    public string targetUserId = "00000001"; 
    public bool syncRotation = true;

    [Header("State Output")]
    [Tooltip("서버에서 수신한 접촉 상태")]
    public bool isContacted = false;

    [Header("Target")]
    public Transform humanDummy;   // HumanDummy 루트 Transform

    [Header("Target Hands")]
    public Transform leftHandTarget; 
    public Transform rightHandTarget;

    [Header("Hand Calibration")]
    public Vector3 handRotationOffset = new Vector3(0, 0, 0);

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
        while (true)
        {
            string url = "";
            bool isAutoMode = string.IsNullOrEmpty(targetUserId);

            // 1. 타겟 ID가 있으면 특정 유저 조회, 없으면 전체 리스트 조회
            if (isAutoMode)
                url = flaskBaseUrl + "/get_users_data";
            else
                url = flaskBaseUrl + "/get_user?id=" + targetUserId;

            UnityWebRequest req = UnityWebRequest.Get(url);
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                string json = req.downloadHandler.text;
                ProcessJson(json, isAutoMode);
            }
            else
            {
                // 연결 실패 시 로그는 너무 자주 뜨지 않게 조절 필요
                Debug.LogWarning("[PositionFollower] GET failed: " + req.error);
            }

            yield return new WaitForSeconds(pollInterval);
        }
    }

    void ProcessJson(string json, bool isAutoMode)
    {
        try
        {
            UserData data = null;

            if (isAutoMode)
            {
                // [Auto Mode] 기존 로직 유지: ID만 찾아서 targetUserId 설정 후 리턴
                if (json.Length < 5) return; 
                
                // 간단 파싱으로 ID 추출 시도
                int hmdIndex = json.IndexOf("\"hmd\"");
                if (hmdIndex != -1)
                {
                    int quoteStart = json.IndexOf('"');
                    int quoteEnd = json.IndexOf('"', quoteStart + 1);
                    if (quoteStart != -1 && quoteEnd != -1)
                    {
                        string firstKey = json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                        if (!string.IsNullOrEmpty(firstKey) && firstKey != "error")
                        {
                            targetUserId = firstKey;
                            Debug.Log($"[PositionFollower] Auto-detected User ID: {targetUserId}");
                        }
                    }
                }
                return;
            }
            else
            {
                // [Specific ID Mode] 단일 유저 데이터 JSON: { "hmd": {...}, "is_contact": ... }
                data = JsonUtility.FromJson<UserData>(json);
            }

            // 위치 적용
            if (data != null)
            {
                isContacted = data.is_contact;

                // 1. HMD (Body) 위치 적용
                if (data.hmd != null)
                {
                    Vector3 newPos = data.hmd.pos.ToVector3();
                    humanDummy.position = new Vector3(newPos.x, 0, newPos.z);

                    if (syncRotation)
                    {
                        Quaternion rawRot = data.hmd.rot.ToQuaternion();
                        Vector3 euler = rawRot.eulerAngles;
                        humanDummy.rotation = Quaternion.Euler(0, euler.y, 0);
                    }
                }

                Quaternion correction = Quaternion.Euler(handRotationOffset);

                // 2. Left Hand
                if (leftHandTarget != null && data.left_hand != null && data.left_hand.Length > 0)
                {
                    leftHandTarget.position = data.left_hand[0].pos.ToVector3();
                    
                    Quaternion rawRot = data.left_hand[0].rot.ToQuaternion();
                    leftHandTarget.rotation = rawRot * correction; 
                }

                // 3. Right Hand
                if (rightHandTarget != null && data.right_hand != null && data.right_hand.Length > 0)
                {
                    rightHandTarget.position = data.right_hand[0].pos.ToVector3();
                    
                    Quaternion rawRot = data.right_hand[0].rot.ToQuaternion();
                    rightHandTarget.rotation = rawRot * correction;
                }
            }
        }
        catch (System.Exception e)
        {
            // Debug.LogWarning("Parse Error: " + e.Message);
        }
    }
}
