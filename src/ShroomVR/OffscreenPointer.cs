using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ShroomVR;

/// <summary>
/// On a monitor nothing interactive is visible outside the game camera's frame, so Unity's physics
/// raycasters (and the game's own pointer helpers) drop pointer positions outside the screen. In VR
/// the headset shows far more than that frame: the hand of cards below it, the ends of a shop row,
/// doors to the side.
///
/// For an off-screen pointer, the raycaster's camera is turned for the duration of that one call so
/// the very same world ray passes through the screen centre, and the event position is moved there.
/// Unity's own raycaster then runs unchanged and builds its results natively; afterwards camera and
/// position are restored. (Patching ComputeRayAndDistance directly is impossible: Il2CppInterop can't
/// marshal its ref Ray. Building RaycastResults in managed code crashes the engine later.)
/// </summary>
[HarmonyPatch]
public static class OffscreenPointer
{
    public struct State
    {
        public bool Active;
        public Transform CameraTransform;
        public Quaternion SavedRotation;
        public Vector2 SavedPosition;
        public PointerEventData Event;
    }

    static void Begin(BaseRaycaster raycaster, PointerEventData eventData,
                      Il2CppSystem.Collections.Generic.List<RaycastResult> results, out State state)
    {
        state = default;
        if (!VRSystem.Enabled || eventData == null) return;
        var cam = raycaster.eventCamera;
        if (cam == null) return;
        var pos = eventData.position;
        if (cam.pixelRect.Contains(pos) || !float.IsFinite(pos.x) || !float.IsFinite(pos.y)) return;

        var ray = cam.ScreenPointToRay(pos);
        if (Vector3.Dot(ray.direction, cam.transform.forward) <= 0.01f) return; // behind the camera

        var t = cam.transform;
        state = new State
        {
            Active = true,
            CameraTransform = t,
            SavedRotation = t.rotation,
            SavedPosition = pos,
            Event = eventData,
        };
        t.rotation = Quaternion.LookRotation(ray.direction, t.up);
        eventData.position = cam.pixelRect.center;
    }

    static void End(Il2CppSystem.Collections.Generic.List<RaycastResult> results, State state)
    {
        if (!state.Active) return;
        state.CameraTransform.rotation = state.SavedRotation;
        state.Event.position = state.SavedPosition;
        // Results are left exactly as Unity built them (their screenPosition is the centre point).
    }

    [HarmonyPatch(typeof(PhysicsRaycaster), nameof(PhysicsRaycaster.Raycast))]
    [HarmonyPrefix]
    static void PhysicsBegin(PhysicsRaycaster __instance, PointerEventData eventData,
                             Il2CppSystem.Collections.Generic.List<RaycastResult> resultAppendList, out State __state) =>
        Begin(__instance, eventData, resultAppendList, out __state);

    [HarmonyPatch(typeof(PhysicsRaycaster), nameof(PhysicsRaycaster.Raycast))]
    [HarmonyPostfix]
    static void PhysicsEnd(Il2CppSystem.Collections.Generic.List<RaycastResult> resultAppendList, State __state) =>
        End(resultAppendList, __state);

    [HarmonyPatch(typeof(Physics2DRaycaster), nameof(Physics2DRaycaster.Raycast))]
    [HarmonyPrefix]
    static void Physics2DBegin(Physics2DRaycaster __instance, PointerEventData eventData,
                               Il2CppSystem.Collections.Generic.List<RaycastResult> resultAppendList, out State __state) =>
        Begin(__instance, eventData, resultAppendList, out __state);

    [HarmonyPatch(typeof(Physics2DRaycaster), nameof(Physics2DRaycaster.Raycast))]
    [HarmonyPostfix]
    static void Physics2DEnd(Il2CppSystem.Collections.Generic.List<RaycastResult> resultAppendList, State __state) =>
        End(resultAppendList, __state);

    /// <summary>The game's own check before it turns the pointer into a world ray (hover, aiming, torch).</summary>
    [HarmonyPatch(typeof(CameraPointerExtensions), nameof(CameraPointerExtensions.CanQueryScreenPoint))]
    [HarmonyPrefix]
    static bool CanQueryScreenPoint(Camera camera, Vector2 screenPoint, ref bool __result)
    {
        if (!VRSystem.Enabled) return true;
        __result = float.IsFinite(screenPoint.x) && float.IsFinite(screenPoint.y) && CameraPointerExtensions.CanQueryCamera(camera);
        return false;
    }
}
