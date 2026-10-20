using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShroomVR;

/// <summary>
/// Turns the game's screen-space canvases into a world-space "virtual screen": each canvas is placed
/// at a comfortable distance in front of the flat reference camera and scaled so it covers exactly
/// the area of the camera's frustum it covered on the monitor. Because it is a radial rescale about
/// the camera, every screen coordinate still maps to the same UI element, so the game's raycasts,
/// tooltips and card layout code keep working unchanged.
/// </summary>
public static class VRUI
{
    class Entry
    {
        public Canvas Canvas;
        public RenderMode OriginalMode;
        public Camera OriginalCamera;
        public float OriginalPlane;
        public int OriginalLayer;
        public int OriginalOrder;
    }

    static readonly Dictionary<IntPtr, Entry> Entries = new();
    static readonly List<Canvas> GameWorldCanvases = new();
    static readonly List<Canvas> _interactive = new();
    static readonly List<Entry> Ordered = new();
    static float _nextScan;

    public static IReadOnlyList<CanvasPlane> Planes => _planes;
    static readonly List<CanvasPlane> _planes = new();

    /// <summary>A placed virtual-screen canvas, for laser hit testing.</summary>
    public struct CanvasPlane
    {
        public Canvas Canvas;
        public Vector3 Center;
        public Vector3 Normal;
        public Camera EventCamera;
    }

    public static void Init()
    {
        SceneManager.sceneLoaded = Combine(SceneManager.sceneLoaded, new Action<Scene, LoadSceneMode>((_, _) => _nextScan = 0));
    }

    static UnityEngine.Events.UnityAction<Scene, LoadSceneMode> Combine(UnityEngine.Events.UnityAction<Scene, LoadSceneMode> a, Action<Scene, LoadSceneMode> b)
    {
        UnityEngine.Events.UnityAction<Scene, LoadSceneMode> bb = b;
        return a == null ? bb : Il2CppSystem.Delegate.Combine(a, bb).Cast<UnityEngine.Events.UnityAction<Scene, LoadSceneMode>>();
    }

    static void Scan()
    {
        GameWorldCanvases.Clear();
        foreach (var cv in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (cv == null || cv.gameObject.scene.name == null) continue; // prefabs / assets
            if (Entries.ContainsKey(cv.Pointer)) continue;
            if (cv.renderMode == RenderMode.WorldSpace)
            {
                // The game's own world-space panels: the laser hit-tests them directly.
                if (cv.GetComponent<GraphicRaycaster>() != null) GameWorldCanvases.Add(cv);
                continue;
            }
            if (cv.transform.parent != null && cv.transform.parent.GetComponentInParent<Canvas>(true) != null) continue; // nested
            Entries[cv.Pointer] = new Entry
            {
                Canvas = cv,
                OriginalMode = cv.renderMode,
                OriginalCamera = cv.worldCamera,
                OriginalPlane = cv.planeDistance,
                OriginalLayer = cv.gameObject.layer,
                OriginalOrder = cv.sortingOrder,
            };
        }

        Ordered.Clear();
        var dead = new List<IntPtr>();
        foreach (var kv in Entries)
        {
            if (kv.Value.Canvas == null) dead.Add(kv.Key);
            else Ordered.Add(kv.Value);
        }
        foreach (var k in dead) Entries.Remove(k);
        // Overlay canvases were drawn over camera canvases; higher sorting order on top.
        Ordered.Sort((a, b) =>
        {
            int ao = a.OriginalMode == RenderMode.ScreenSpaceOverlay ? 1 : 0, bo = b.OriginalMode == RenderMode.ScreenSpaceOverlay ? 1 : 0;
            if (ao != bo) return ao.CompareTo(bo);
            int c = a.OriginalOrder.CompareTo(b.OriginalOrder);
            return c != 0 ? c : b.OriginalPlane.CompareTo(a.OriginalPlane);
        });
    }

    /// <summary>Active canvases the laser can point at (virtual screen first, then world-space panels).</summary>
    public static List<Canvas> InteractiveCanvases()
    {
        _interactive.Clear();
        foreach (var p in _planes)
            if (p.Canvas != null && p.Canvas.GetComponent<GraphicRaycaster>() is { enabled: true }) _interactive.Add(p.Canvas);
        foreach (var cv in GameWorldCanvases)
        {
            if (cv == null || !cv.isActiveAndEnabled) continue;
            var gr = cv.GetComponent<GraphicRaycaster>();
            if (gr != null && gr.enabled) _interactive.Add(cv);
        }
        return _interactive;
    }

    /// <summary>Called from onBeforeRender with flat cameras.</summary>
    public static void Place()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 0.5f;
            Scan();
        }

        _planes.Clear();
        var reference = VRRig.ReferenceCamera;
        float baseDistance = Mathf.Max(0.3f, VRConfig.UiDistance.Value) * VRRig.Scale;
        int rank = 0;
        foreach (var e in Ordered)
        {
            var cv = e.Canvas;
            if (cv == null || !cv.gameObject.activeInHierarchy) continue;
            try
            {
                var cam = e.OriginalMode == RenderMode.ScreenSpaceCamera && e.OriginalCamera != null && e.OriginalCamera.isActiveAndEnabled
                    ? e.OriginalCamera
                    : UiCameraFor(reference);
                if (cam == null) continue;
                Convert(e, cam);
                float dist = baseDistance - 0.003f * rank++;
                PlaceOnScreen(cv, cam, dist);
                _planes.Add(new CanvasPlane { Canvas = cv, Center = cv.transform.position, Normal = cv.transform.forward, EventCamera = cam });
            }
            catch (Exception ex)
            {
                VRRig.LogOnce("VRUI " + cv.name, ex);
            }
        }
    }

    /// <summary>Overlay canvases need a camera that draws after the world with a cleared depth buffer.</summary>
    static Camera UiCameraFor(Camera reference)
    {
        if (reference == null) return null;
        foreach (var c in Cameras.Enabled())
        {
            if (c.targetTexture == null && (c.cullingMask & (1 << VRLayers.UI)) != 0 && c.clearFlags == CameraClearFlags.Depth &&
                (c.transform == reference.transform || c.transform.IsChildOf(reference.transform)))
                return c;
        }
        return (reference.cullingMask & (1 << VRLayers.UI)) != 0 ? reference : null;
    }

    static void Convert(Entry e, Camera cam)
    {
        var cv = e.Canvas;
        if (cv.renderMode != RenderMode.WorldSpace)
        {
            var scaler = cv.GetComponent<CanvasScaler>();
            float scale = cv.scaleFactor;
            cv.renderMode = RenderMode.WorldSpace;
            if (scaler != null) scaler.dynamicPixelsPerUnit = Mathf.Max(2f, scale * 2f);
        }
        if (cv.worldCamera != cam) cv.worldCamera = cam;
        if (e.OriginalMode == RenderMode.ScreenSpaceOverlay)
        {
            int layer = (cam.cullingMask & (1 << VRLayers.UI)) != 0 ? VRLayers.UI : FirstLayer(cam.cullingMask);
            if (cv.gameObject.layer != layer) cv.gameObject.layer = layer;
            if (cv.sortingOrder != e.OriginalOrder + 1000) cv.sortingOrder = e.OriginalOrder + 1000;
        }
    }

    static int FirstLayer(int mask)
    {
        for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
        return 0;
    }

    static void PlaceOnScreen(Canvas cv, Camera cam, float dist)
    {
        var rt = cv.GetComponent<RectTransform>();
        var size = CanvasSize(cv);
        if (rt.sizeDelta != size) rt.sizeDelta = size;
        rt.pivot = new Vector2(0.5f, 0.5f);

        var ct = cam.transform;
        float height = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float s = height / Mathf.Max(1f, size.y);
        cv.transform.SetPositionAndRotation(ct.position + ct.forward * dist, ct.rotation);
        var parent = cv.transform.parent;
        var ps = parent != null ? parent.lossyScale : Vector3.one;
        cv.transform.localScale = new Vector3(s / NonZero(ps.x), s / NonZero(ps.y), s / NonZero(ps.z));
    }

    static float NonZero(float v) => Mathf.Abs(v) < 1e-6f ? 1e-6f : v;

    /// <summary>The size in canvas units a screen-space canvas would have had for the current window.</summary>
    static Vector2 CanvasSize(Canvas cv)
    {
        var screen = new Vector2(Screen.width, Screen.height);
        var scaler = cv.GetComponent<CanvasScaler>();
        float sf = 1f;
        if (scaler != null && scaler.enabled)
        {
            switch (scaler.uiScaleMode)
            {
                case CanvasScaler.ScaleMode.ConstantPixelSize:
                    sf = scaler.scaleFactor;
                    break;
                case CanvasScaler.ScaleMode.ScaleWithScreenSize:
                    var r = scaler.referenceResolution;
                    switch (scaler.screenMatchMode)
                    {
                        case CanvasScaler.ScreenMatchMode.Expand:
                            sf = Mathf.Min(screen.x / r.x, screen.y / r.y);
                            break;
                        case CanvasScaler.ScreenMatchMode.Shrink:
                            sf = Mathf.Max(screen.x / r.x, screen.y / r.y);
                            break;
                        default:
                            float lw = Mathf.Log(screen.x / r.x, 2), lh = Mathf.Log(screen.y / r.y, 2);
                            sf = Mathf.Pow(2, Mathf.Lerp(lw, lh, scaler.matchWidthOrHeight));
                            break;
                    }
                    break;
            }
        }
        return screen / Mathf.Max(0.01f, sf);
    }
}
