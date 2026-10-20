using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// Cards in your hands. The hand of cards is held in the off hand, and a card you are playing sits in
/// the laser hand instead of trailing the game's dotted cast line.
///
/// The game lays the hand out in front of its camera ("[ExploreHand]RotationRoot" and friends follow the
/// camera) and measures that layout on screen every frame. So nothing is moved for the game: between
/// onBeforeRender and the end of the frame each hand root is moved by one similarity transform that takes
/// the game's hand area to the controller (turned so you see it as the camera would), and the card being
/// cast is posed in the other hand; afterwards everything is put back. The laser maps its beam through
/// the same transform (<see cref="TryHit"/>), so it touches the cards' colliders where the game keeps
/// them and the game sees an ordinary pointer over the card.
/// </summary>
public static class CardHand
{
    static readonly string[] RootNames = { "[ExploreHand]RotationRoot", "[CombatHand]RotationRoot", "[ForesightHand]RotationRoot" };
    static readonly Transform[] _roots = new Transform[RootNames.Length];
    static float _nextLookup;

    /// <summary>Where the game's hand area sits in its root (RotationOffset's resting local position).</summary>
    static readonly Vector3 Pivot = new(0f, -0.32f, 0.55f);

    struct Shown
    {
        public Transform Root;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public Matrix4x4 World; // the root's matrix while rendered
    }

    static readonly List<Shown> _shown = new();
    /// <summary>Roots as shown last frame, kept for the laser after they are put back.</summary>
    static readonly List<Shown> _lastShown = new();

    static Transform _heldCard;
    static Vector3 _heldPos, _heldScale;
    static Quaternion _heldRot;

    static Light _cardLight;
    static Vector3 _cardLightPos;
    static float _cardLightRange;
    static bool _cardLightMoved;

    static V_PlayerSpellCaster _caster;

    // Card silhouette overlays (OverlaySilouette) also draw a goo silhouette over any card closer than about
    // half a metre to the camera; cards held in VR always are. Hidden while shown in a hand, unless the
    // game itself fades the silhouette in.
    static readonly List<(OverlaySilouette owner, List<SpriteRenderer> sprites)> _silhouettes = new();
    static readonly List<SpriteRenderer> _hiddenSilhouettes = new();
    static float _nextSilhouetteLookup;

    static void FindRoots()
    {
        if (Time.unscaledTime < _nextLookup) return;
        _nextLookup = Time.unscaledTime + 1f;
        for (int i = 0; i < RootNames.Length; i++)
        {
            if (_roots[i] != null) continue;
            var go = GameObject.Find(RootNames[i]);
            if (go != null) _roots[i] = go.transform;
        }
        if (_caster == null) _caster = V_PlayerSpellCaster._playerSpellCasterStaticRef;
        if (_cardLight == null)
        {
            var light = UnityEngine.Object.FindObjectOfType<CardLight>();
            if (light != null) _cardLight = light.GetComponent<Light>();
        }
    }

    /// <summary>The hand of cards' pose for this frame: just above the controller, facing the eyes.</summary>
    static Matrix4x4 HandDisplay(HandRig hand)
    {
        float k = Mathf.Max(0.1f, VRConfig.CardHandScale.Value) * VRRig.Scale;
        var grip = hand.Grip;
        var anchor = grip.position + grip.rotation * new Vector3(0f, 0.06f, 0.05f) * VRRig.Scale;
        var toAnchor = anchor - VRRig.HeadPosition;
        if (toAnchor.sqrMagnitude < 1e-6f) toAnchor = grip.forward;
        // The camera sees its hand along Pivot; you see this one along eye -> anchor.
        var seenInGame = Quaternion.LookRotation(Pivot.normalized, Vector3.up);
        var seenHere = Quaternion.LookRotation(toAnchor.normalized, grip.rotation * Vector3.up);
        var rot = seenHere * Quaternion.Inverse(seenInGame);
        return Matrix4x4.TRS(anchor, rot, Vector3.one * k) * Matrix4x4.Translate(-Pivot);
    }

    /// <summary>Render time, after the hands have their poses.</summary>
    public static void Apply(HandRig cardHand, HandRig laserHand)
    {
        _shown.Clear();
        try
        {
            FindRoots();
            HideCastLines();
            if (VRConfig.CardsInHand.Value && cardHand != null && cardHand.Tracked)
            {
                var display = HandDisplay(cardHand);
                foreach (var root in _roots)
                {
                    if (root == null || !root.gameObject.activeInHierarchy) continue;
                    var s = new Shown { Root = root, Pos = root.position, Rot = root.rotation, Scale = root.localScale, World = display };
                    _shown.Add(s);
                }
                // Grab the card's game scale before its root is scaled.
                var held = HeldCard();
                var heldLossy = held != null ? held.lossyScale : Vector3.one;
                foreach (var s in _shown)
                {
                    s.Root.SetPositionAndRotation(display.MultiplyPoint3x4(Vector3.zero), display.rotation);
                    s.Root.localScale = Vector3.Scale(s.Scale, display.lossyScale);
                }
                MoveCardLight(display);
                HoldCard(held, heldLossy, laserHand);
            }
            else
            {
                var held = HeldCard();
                HoldCard(held, held != null ? held.lossyScale : Vector3.one, laserHand);
            }
            HideNearSilhouettes();
            _lastShown.Clear();
            _lastShown.AddRange(_shown);
        }
        catch (Exception e)
        {
            VRRig.LogOnce("card hand", e);
        }
    }

    /// <summary>The card the player is casting (picked up from the hand, not yet played), if any.</summary>
    public static Transform CastingCard()
    {
        if (_caster == null) return null;
        var model = _caster.Model;
        if (model == null || !model.IsCasting || model.CastingCard == null) return null;
        // The view keeps its last casting card after the cast; only trust it while the model casts it.
        var view = _caster._castingVICard?.TryCast<V_GameplayCardInstance>();
        if (view == null || view.ModelImpl == null || view.ModelImpl.Pointer != model.CastingCard.Pointer) return null;
        return view.gameObject.activeInHierarchy ? view.transform : null;
    }

    static Transform HeldCard() => VRConfig.HoldCastCard.Value ? CastingCard() : null;

    static void HoldCard(Transform card, Vector3 gameLossy, HandRig hand)
    {
        _heldCard = null;
        if (card == null || hand == null || !hand.Tracked) return;
        _heldCard = card;
        _heldPos = card.localPosition;
        _heldRot = card.localRotation;
        _heldScale = card.localScale;

        // Pinched just above the controller, face towards you.
        var aim = hand.Aim;
        var pos = aim.position + aim.rotation * new Vector3(0f, 0.045f, 0.06f) * VRRig.Scale;
        var toCard = pos - VRRig.HeadPosition;
        if (toCard.sqrMagnitude < 1e-6f) toCard = aim.forward;
        card.SetPositionAndRotation(pos, Quaternion.LookRotation(toCard.normalized, aim.rotation * Vector3.up));
        float want = Mathf.Max(0.05f, VRConfig.HeldCardScale.Value) * VRRig.Scale * gameLossy.x;
        float now = card.lossyScale.x;
        if (Mathf.Abs(now) > 1e-6f) card.localScale = _heldScale * (want / now);
    }

    /// <summary>The game's light for the cards follows them (its range scaled with the hand).</summary>
    static void MoveCardLight(Matrix4x4 display)
    {
        _cardLightMoved = false;
        if (_cardLight == null || !_cardLight.isActiveAndEnabled || _shown.Count == 0) return;
        var lit = _shown[0];
        foreach (var s in _shown) if (HasCards(s.Root)) { lit = s; break; }
        var gameRoot = Matrix4x4.TRS(lit.Pos, lit.Rot, lit.Scale);
        var toShown = display * gameRoot.inverse;
        var t = _cardLight.transform;
        _cardLightPos = t.position;
        _cardLightRange = _cardLight.range;
        t.position = toShown.MultiplyPoint3x4(_cardLightPos);
        _cardLight.range = _cardLightRange * display.lossyScale.x;
        _cardLightMoved = true;
    }

    static void HideNearSilhouettes()
    {
        if (_shown.Count == 0 && _heldCard == null) return;
        if (Time.unscaledTime >= _nextSilhouetteLookup)
        {
            _nextSilhouetteLookup = Time.unscaledTime + 0.5f;
            _silhouettes.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType<OverlaySilouette>())
            {
                var sprites = new List<SpriteRenderer>();
                foreach (var sr in o.GetComponentsInChildren<SpriteRenderer>(true)) sprites.Add(sr); // CardBase/CardCrown silhouettes
                _silhouettes.Add((o, sprites));
            }
        }
        foreach (var (o, sprites) in _silhouettes)
        {
            if (o == null || o.Visibility > 0.01f) continue;
            var t = o.transform;
            bool displaced = _heldCard != null && t.IsChildOf(_heldCard);
            foreach (var sh in _shown) if (!displaced && t.IsChildOf(sh.Root)) displaced = true;
            if (!displaced) continue;
            foreach (var sr in sprites)
            {
                if (sr == null || sr.forceRenderingOff) continue;
                sr.forceRenderingOff = true;
                _hiddenSilhouettes.Add(sr);
            }
        }
    }

    /// <summary>Any active card holder under the root's offset?</summary>
    static bool HasCards(Transform root)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            var offset = root.GetChild(i);
            for (int j = 0; j < offset.childCount; j++)
                if (offset.GetChild(j).gameObject.activeSelf) return true;
        }
        return false;
    }

    /// <summary>The game's cast lines run from the card's on-screen spot to the pointer; the held card and laser replace them.</summary>
    static void HideCastLines()
    {
        if (_caster == null) return;
        bool hide = VRConfig.HoldCastCard.Value;
        Hide(_caster.Line, hide);
        Hide(_caster.RandomLine, hide);
        Hide(_caster.AllLine, hide);
        Hide(_caster.HealthCastLine, hide);
        Hide(_caster.EnergyCastLine, hide);
        HideAll(_caster._extraLines, hide);
        HideAll(_caster._loopyExtraLines, hide);
        HideAll(_caster._allExtraLines, hide);
    }

    static void Hide(LineRenderer line, bool hide)
    {
        if (line != null && line.forceRenderingOff != hide) line.forceRenderingOff = hide;
    }

    static void HideAll(Il2CppSystem.Collections.Generic.List<LineRenderer> lines, bool hide)
    {
        if (lines == null) return;
        for (int i = 0; i < lines.Count; i++) Hide(lines[i], hide);
    }

    /// <summary>End of frame: give the game its layout back.</summary>
    public static void Restore()
    {
        try
        {
            if (_heldCard != null)
            {
                _heldCard.localPosition = _heldPos;
                _heldCard.localRotation = _heldRot;
                _heldCard.localScale = _heldScale;
                _heldCard = null;
            }
            if (_cardLightMoved && _cardLight != null)
            {
                _cardLight.transform.position = _cardLightPos;
                _cardLight.range = _cardLightRange;
            }
            _cardLightMoved = false;
            foreach (var sr in _hiddenSilhouettes) if (sr != null) sr.forceRenderingOff = false;
            _hiddenSilhouettes.Clear();
            foreach (var s in _shown)
            {
                if (s.Root == null) continue;
                s.Root.SetPositionAndRotation(s.Pos, s.Rot);
                s.Root.localScale = s.Scale;
            }
            _shown.Clear();
        }
        catch (Exception e)
        {
            VRRig.LogOnce("card hand restore", e);
        }
    }

    /// <summary>Is this collider part of a hand of cards that is shown somewhere else than the game keeps it?</summary>
    public static bool IsDisplaced(Transform t)
    {
        foreach (var s in _lastShown)
            if (s.Root != null && t.IsChildOf(s.Root)) return true;
        return false;
    }

    /// <summary>Where a point of a displaced hand is shown (for aiming tools); unchanged otherwise.</summary>
    public static Vector3 ToShown(Transform t, Vector3 gamePoint)
    {
        foreach (var s in _lastShown)
            if (s.Root != null && t.IsChildOf(s.Root))
                return (s.World * s.Root.worldToLocalMatrix).MultiplyPoint3x4(gamePoint);
        return gamePoint;
    }

    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in _lastShown)
            sb.AppendLine($"shown {s.Root?.name} game={s.Pos} shownAt={s.World.MultiplyPoint3x4(Pivot)} scale={s.World.lossyScale.x:F2} cards={(s.Root != null && HasCards(s.Root))}");
        var model = _caster != null ? _caster.Model : null;
        sb.AppendLine($"casting={(model != null && model.IsCasting)} held={(HeldCard() != null)} light={(_cardLight != null ? _cardLight.name : "-")}");
        return sb.ToString();
    }

    // Must be a native array: a managed RaycastHit[] is copied into a temporary and never copied back.
    static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> _hits = new(32);

    /// <summary>
    /// The beam against the cards as you see them in your hand: the ray is taken into the game's frame of
    /// each shown hand and cast at the real colliders there.
    /// </summary>
    /// <param name="exclude">The card being cast: it is in your other hand, not in this one.</param>
    public static bool TryHit(Vector3 origin, Vector3 dir, float range, int mask, Transform exclude, out Vector3 gamePoint, out float shownDistance)
    {
        gamePoint = default;
        shownDistance = float.MaxValue;
        bool found = false;
        foreach (var s in _lastShown)
        {
            if (s.Root == null || !s.Root.gameObject.activeInHierarchy) continue;
            var toGame = s.Root.localToWorldMatrix * s.World.inverse;
            var o = toGame.MultiplyPoint3x4(origin);
            var d = toGame.MultiplyVector(dir);
            float len = d.magnitude;
            if (len < 1e-6f) continue;
            d /= len;
            int n = Physics.RaycastNonAlloc(new Ray(o, d), _hits, range * len, mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider == null || !h.collider.transform.IsChildOf(s.Root)) continue;
                if (exclude != null && h.collider.transform.IsChildOf(exclude)) continue;
                float shown = h.distance / len;
                if (shown < shownDistance) { shownDistance = shown; gamePoint = h.point; found = true; }
            }
        }
        return found;
    }
}
