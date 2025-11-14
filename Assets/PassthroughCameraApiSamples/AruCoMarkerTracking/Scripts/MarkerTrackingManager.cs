using UnityEngine;
using MarkerTracking;
using UnityEngine.Assertions;
using System.Collections;
using PassthroughCameraSamples;
using System.Collections.Generic;
using System;
using Meta.XR.Samples;
using UnityEngine.UI;

public class MarkerTrackingManager : MonoBehaviour
{
    [Serializable] public class MarkerObjects
    {
        public int markerId;
        public GameObject marker;
    }
    [SerializeField] private WebCamTextureManager m_webCamTextureManager;
    [SerializeField] private Transform m_cameraAnchor;
    private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;
    [SerializeField] private ArUcoMarkerTracking m_arUcoMarkerTracking;
    [SerializeField] private RawImage m_resultRawImage;
    [SerializeField] private List<MarkerObjects> m_markerObjects = new List<MarkerObjects>();
    private Dictionary<int, GameObject> m_arObjects = new Dictionary<int, GameObject>();
    private Texture2D m_resultTexture;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private IEnumerator Start()
    {
        if (m_webCamTextureManager == null)
        {
            Debug.LogError($"PCA: {nameof(m_webCamTextureManager)} field is required ");
            enabled = false;
            yield break;
        }

        // Make sure the manager is disabled in scene and enable it only when the required permissions have been granted
        Assert.IsFalse(m_webCamTextureManager.enabled);
        while (PassthroughCameraPermissions.HasCameraPermission != true)
        {
            yield return null;
        }
        
        // Set the 'requestedResolution' and enable the manager
        m_webCamTextureManager.RequestedResolution = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye).Resolution;
        m_webCamTextureManager.enabled = true;

        while(m_webCamTextureManager.WebCamTexture == null)
        {
            yield return null;
        }

        InitializeMarkerTracking();
    }

    private void InitializeMarkerTracking()
    {
        var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
        var cx = intrinsics.PrincipalPoint.x;
        var cy = intrinsics.PrincipalPoint.y;
        var fx = intrinsics.FocalLength.x;
        var fy = intrinsics.FocalLength.y;
        var width = intrinsics.Resolution.x;
        var height = intrinsics.Resolution.y;

        m_arUcoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);
        
        m_arObjects.Clear();
        foreach (var markerObject in m_markerObjects)
        {
            if (markerObject.marker != null)
            {
                m_arObjects[markerObject.markerId] = markerObject.marker;
            }
        }

        int divideNumber = m_arUcoMarkerTracking.DivideNumber;
        m_resultTexture = new Texture2D(width / divideNumber, height / divideNumber, TextureFormat.RGB24, false);
        m_resultRawImage.texture = m_resultTexture;
    }

    // Update is called once per frame
    void Update()
    {
        if (m_webCamTextureManager.WebCamTexture == null || !m_arUcoMarkerTracking.IsReady)
            return;

        var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
        m_cameraAnchor.position = cameraPose.position;
        m_cameraAnchor.rotation = cameraPose.rotation;
        
        m_arUcoMarkerTracking.DetectMarker(m_webCamTextureManager.WebCamTexture, m_resultTexture);
        m_arUcoMarkerTracking.EstimatePoseCanonicalMarker(m_arObjects, m_cameraAnchor);
    }
}
