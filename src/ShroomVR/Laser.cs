using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShroomVR;

/// <summary>
/// The controller laser is the game's mouse. Each frame it finds what the beam points at (cards first,
/// then UI on the virtual screen, then the world), projects that point through the flat game camera
/// and feeds the resulting screen position to both the game's pointer and the virtual Input System
/// mouse. The game then hovers, drags and targets exactly as it would with a mouse.
/// </summary>
public class Laser
{
    public static Laser Instance { get; private set; }

    public Hand ActiveHand { get; private set; }
    public bool HasTarget { get; private set; }
    public Vector2 ScreenPoint { get; private set; }
    public HitKind Kind { get; private set; }
    public enum HitKind { None, Card, UI, World, Nothing }

    /// <summary>Console override of the pointer (screen pixels, bottom-left origin) for testing.</summary>
    public Vector2? DebugScreen;

    float _distance = 2f;
    GameObject _hovered;
    LineRenderer _line;
    Transform _dot;
    Material _material;
    readonly Il2CppSystem.Collections.Generic.List<RaycastResult> _results = new();
    PointerEventData _ped;


    static readonly Color BeamColor = new(0.55f, 1f, 0.45f, 0.85f);
    static readonly Color BeamPressedColor = new(1f, 0.95f, 0.35f, 1f);

    public static void Create()
    {
        Instance = new Laser { ActiveHand = VRConfig.LeftHanded.Value ? Hand.Left : Hand.Right };
    }

    HandRig Rig(Hand h) => h == Hand.Left ? VRRig.Instance.Left : VRRig.Instance.Right;

    // ------------------------------------------------------------------ targeting (logic time) --

    readonly System.Diagnostics.Stopwatch _watch = new();
    public double AverageMs { get; private set; }

    public void UpdateTargeting()
    {
        _watch.Restart();
        try { Target(); TrackGrab(); }
        finally { AverageMs = AverageMs * 0.95 + _watch.Elapsed.TotalMilliseconds * 0.05; }
    }

    void Target()
    {
        if (DebugScreen.HasValue)
        {
            ScreenPoint = DebugScreen.Value;
            HasTarget = true;
            Kind = HitKind.UI;
            VirtualMouse.SetPosition(ScreenPoint);
            VirtualMouse.SetButton(0, InputBridge.DebugHeld(InputActionId.Interact));
            return;
        }
        if (VRRig.Instance == null) return;

        // Whichever hand pulls the trigger owns the laser; the bumper swaps explicitly.
        var other = ActiveHand == Hand.Left ? Hand.Right : Hand.Left;
        if (VRInput.GetDown(VRAction.Click, other) && Rig(other).Tracked && !VirtualMouse.LeftDown) ActiveHand = other;
        else if (VRInput.GetDownAny(VRAction.ToggleLaserHand)) ActiveHand = other;


        var hand = Rig(ActiveHand);
        var cam = VRRig.InteractionCamera ?? VRRig.ReferenceCamera;
        HasTarget = false;
        if (!hand.Tracked || cam == null)
        {
            Kind = HitKind.None;
            return;
        }

        var origin = hand.Aim.position;
        var dir = hand.Aim.forward;
        bool dragging = VirtualMouse.LeftDown;
        float scale = VRRig.Scale;

        // Mirror what the game's own raycasters test, but along the beam: panels and the card/UI layers
        // are drawn over the world by the UI camera, so the nearest of those wins; the world only
        // counts when the beam misses all of them.
        Vector3 point;
        Camera projectCam = cam;
        float best = float.MaxValue;
        point = default;
        Kind = HitKind.Nothing;

        if (TryUi(cam, origin, dir, out var uiPoint, out var uiCam, out var uiDist))
        {
            best = uiDist; point = uiPoint; projectCam = uiCam; Kind = HitKind.UI;
        }
        int overlayMask = VRRig.InOverlayView ? OverlayMask | (1 << VRLayers.DeckDisplay) : OverlayMask;
        if (Nearest(origin, dir, 10f * scale, overlayMask, dragging, out var overlayPoint, out var overlayDist, out var overlayLayer) && overlayDist < best)
        {
            best = overlayDist; point = overlayPoint; projectCam = cam;
            Kind = overlayLayer == VRLayers.Card ? HitKind.Card : HitKind.UI;
        }
        // The hand of cards is shown in your other hand: test the beam there, point where the game keeps it.
        // Also while dragging (a card cast onto another card in hand): the dragged card is not in that hand.
        float shownDistance = -1f;
        if (CardHand.TryHit(origin, dir, 10f * scale, OverlayMask, CardHand.CastingCard(), out var handPoint, out var handDist) && handDist < best)
        {
            best = handDist; point = handPoint; projectCam = cam; Kind = HitKind.Card; shownDistance = handDist;
        }
        // An overlay view (deck) covers the world entirely.
        if (Kind == HitKind.Nothing && !VRRig.InOverlayView && Nearest(origin, dir, MaxRange * scale, WorldMask(), false, out var worldPoint, out _, out _, solidOrTargets: true))
        {
            point = worldPoint; Kind = HitKind.World;
        }
        if (Kind == HitKind.Nothing) point = origin + dir * (MaxRange * scale);

        var sp = projectCam.WorldToScreenPoint(point);

        _distance = shownDistance >= 0 ? shownDistance : Vector3.Distance(origin, point);
        if (sp.z <= 0.01f) return; // pointing behind the game camera
        var screen = new Vector2(Mathf.Clamp(sp.x, -Screen.width * 10f, Screen.width * 10f), Mathf.Clamp(sp.y, -Screen.height * 10f, Screen.height * 10f));
        ScreenPoint = screen;
        HasTarget = true;

        VirtualMouse.SetPosition(screen);
        // While the sticks navigate the laser rests: a mouse button would switch the game back to the mouse.
        bool resting = StickNav.Active;
        VirtualMouse.SetButton(0, !resting && (VRInput.Get(VRAction.Click, ActiveHand) || InputBridge.DebugHeld(InputActionId.Interact)));
        VirtualMouse.SetButton(1, !resting && (VRInput.GetAny(VRAction.Inspect) || InputBridge.DebugHeld(InputActionId.Inspect)));

        if (!resting) TrackHover();
    }

    const float MaxRange = 60f;
    const int OverlayMask = (1 << VRLayers.UI) | (1 << VRLayers.Card) | (1 << VRLayers.WorldUI);

    /// <summary>
    /// Everything the beam can physically touch except the overlay layers: the game resolves card
    /// targets with its own sphere casts (any layer, triggers included) along the pointer ray, so the
    /// pointer must land on whatever surface the beam actually reaches.
    /// </summary>
    static int WorldMask() => ~OverlayMask & ~(1 << 2);

    // Must be a native array: a managed RaycastHit[] is copied into a temporary and never copied back.
    readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> _hits3D = new(32);

    const int SpellTargets = 8;

    /// <summary>
    /// Nearest 3D or 2D collider (triggers included, as the EventSystem raycasters do). In the world,
    /// <paramref name="solidOrTargets"/> skips trigger volumes other than spell targets: rooms are full of
    /// them (dust particles, door and prompt triggers) and stopping there would put the pointer in mid-air.
    /// </summary>
    bool Nearest(Vector3 origin, Vector3 dir, float range, int mask, bool skipCards, out Vector3 point, out float distance, out int layer, bool solidOrTargets = false)
    {
        point = default;
        distance = float.MaxValue;
        layer = -1;
        var ray = new Ray(origin, dir);
        int n = Physics.RaycastNonAlloc(ray, _hits3D, range, mask, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            var h = _hits3D[i];
            if (h.collider == null) continue;
            int l = h.collider.gameObject.layer;
            if (skipCards && l == VRLayers.Card) continue; // a dragged card sits under the pointer
            if (CardHand.IsDisplaced(h.collider.transform)) continue; // seen in your hand instead
            if (solidOrTargets && h.collider.isTrigger && l != SpellTargets) continue;
            if (h.distance < distance) { distance = h.distance; point = h.point; layer = l; }
        }
        foreach (var h in Physics2D.GetRayIntersectionAll(ray, range, mask))
        {
            if (h.collider == null) continue;
            int l = h.collider.gameObject.layer;
            if (skipCards && l == VRLayers.Card) continue;
            if (CardHand.IsDisplaced(h.collider.transform)) continue;
            if (solidOrTargets && h.collider.isTrigger && l != SpellTargets) continue;
            float d = Vector3.Distance(origin, h.point);
            if (d < distance) { distance = d; point = h.point; layer = l; }
        }
        return layer >= 0;
    }

    readonly List<(float d, Vector3 p, Canvas cv)> _planeHits = new();

    /// <summary>
    /// Intersect the beam with every interactive canvas (the virtual screen and the game's world-space
    /// panels, which have no colliders) and confirm with that canvas's own GraphicRaycaster.
    /// </summary>
    bool TryUi(Camera reference, Vector3 origin, Vector3 dir, out Vector3 point, out Camera cam, out float distance)
    {
        point = default;
        cam = null;
        distance = float.MaxValue;
        _planeHits.Clear();
        foreach (var cv in VRUI.InteractiveCanvases())
        {
            var t = cv.transform;
            var normal = t.forward;
            float denom = Vector3.Dot(normal, dir);
            if (Mathf.Abs(denom) <= 1e-4f) continue;
            float d = Vector3.Dot(t.position - origin, normal) / denom;
            if (d > 0) _planeHits.Add((d, origin + dir * d, cv));
        }
        _planeHits.Sort((x, y) => x.d.CompareTo(y.d));
        foreach (var (d, p, cv) in _planeHits)
        {
            var eventCam = cv.worldCamera != null ? cv.worldCamera : reference;
            if (eventCam == null) continue;
            var sp = eventCam.WorldToScreenPoint(p);
            if (sp.z <= 0 || !GraphicUnder(cv, new Vector2(sp.x, sp.y))) continue;
            distance = d; point = p; cam = eventCam;
            return true;
        }
        return false;
    }

    bool GraphicUnder(Canvas cv, Vector2 screen)
    {
        var gr = cv.GetComponent<GraphicRaycaster>();
        var es = EventSystem.current;
        if (gr == null || es == null) return false;
        _ped ??= new PointerEventData(es);
        _ped.position = screen;
        _results.Clear();
        gr.Raycast(_ped, _results);
        return _results.Count > 0;
    }

    /// <summary>Is there a UI graphic at this screen point (belonging to <paramref name="under"/>, if given)?</summary>
    bool UiUnder(Vector2 screen, Transform under = null)
    {
        var es = EventSystem.current;
        if (es == null) return false;
        _ped ??= new PointerEventData(es);
        _ped.position = screen;
        _results.Clear();
        es.RaycastAll(_ped, _results);
        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            if (r.module == null || r.module.TryCast<GraphicRaycaster>() == null || r.gameObject == null) continue;
            if (under == null || r.gameObject.transform.IsChildOf(under)) return true;
        }
        return false;
    }

    void TrackHover()
    {
        var es = EventSystem.current;
        GameObject top = null;
        if (es != null && _results.Count > 0 && _results[0].gameObject != null) top = _results[0].gameObject;
        if (top != _hovered)
        {
            _hovered = top;
            if (top != null) VRInput.Haptic(ActiveHand, 0.01f, 160f, 0.15f * VRConfig.HapticStrength.Value);
        }
    }

    bool _holding;

    /// <summary>The slightest pulse when a card is actually picked up (by the laser or the sticks), not on every click.</summary>
    void TrackGrab()
    {
        bool holding = CardHand.CastingCard() != null;
        if (holding && !_holding) VRInput.Haptic(ActiveHand, 0.04f, 150f, 0.3f * VRConfig.HapticStrength.Value);
        _holding = holding;
    }

    // ------------------------------------------------------------------ visuals (render time) --

    const short OnTop = short.MaxValue;

    void EnsureVisual()
    {
        if (_line != null) return;
        var shader = ControllerModel.PickShader("Sprites/Default", "UI/Default", "Unlit/Color", "Hidden/Internal-Colored");
        _material = new Material(shader) { renderQueue = 4000 };
        _material.hideFlags = HideFlags.DontUnloadUnusedAsset;

        var go = new GameObject("ShroomVR Laser");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.layer = VRLayers.Laser;
        _line = go.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.positionCount = 2;
        _line.material = _material;
        _line.numCapVertices = 2;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        // Drawn after every panel and card the UI cameras render (canvases sort by their order, up to ~1100).
        _line.sortingOrder = OnTop;

        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        UnityEngine.Object.Destroy(dot.GetComponent<Collider>());
        dot.name = "ShroomVR Laser Dot";
        dot.layer = VRLayers.Laser;
        UnityEngine.Object.DontDestroyOnLoad(dot);
        var mr = dot.GetComponent<MeshRenderer>();
        mr.material = _material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.sortingOrder = OnTop;
        _dot = dot.transform;
    }

    public void UpdateVisual()
    {
        try
        {
            EnsureVisual();
            var hand = Rig(ActiveHand);
            bool show = hand.Tracked && HasTarget && !StickNav.Active;
            _line.enabled = show;
            _dot.gameObject.SetActive(show && Kind != HitKind.Nothing);
            if (!show) return;

            var origin = hand.Aim.position;
            var end = origin + hand.Aim.forward * _distance;
            _line.SetPosition(0, origin);
            _line.SetPosition(1, end);
            float w = VRConfig.LaserWidth.Value * VRRig.Scale;
            _line.startWidth = w;
            _line.endWidth = w * 0.5f;
            var c = VirtualMouse.LeftDown ? BeamPressedColor : BeamColor;
            _line.startColor = c;
            _line.endColor = new Color(c.r, c.g, c.b, 0.15f);
            _material.color = Color.white;

            _dot.position = end;
            _dot.localScale = Vector3.one * Mathf.Clamp(0.006f + _distance * 0.006f, 0.006f, 0.05f) * VRRig.Scale;
        }
        catch (Exception e)
        {
            VRRig.LogOnce("Laser visual", e);
        }
    }
}
