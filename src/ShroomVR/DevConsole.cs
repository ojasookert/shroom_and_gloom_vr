using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ShroomVR;

/// <summary>
/// File-driven developer console. Lines written to BepInEx/shroomvr_cmd.txt are executed on the
/// main thread; results are appended to BepInEx/shroomvr_out.txt. Used to inspect the live game
/// (hierarchy, cameras, canvases, component state) without an in-game UI.
/// </summary>
public class DevConsole : MonoBehaviour
{
    public DevConsole(IntPtr ptr) : base(ptr) { }

    static string CmdPath => Path.Combine(Paths.BepInExRootPath, "shroomvr_cmd.txt");
    static string OutPath => Path.Combine(Paths.BepInExRootPath, "shroomvr_out.txt");

    float _nextPoll;
    (string card, string target, float start)? _drag;

    void StepDrag()
    {
        if (_drag == null) return;
        var (card, target, start) = _drag.Value;
        float t = Time.unscaledTime - start;
        if (t < 0.3f) Commands["aimat"](this, card.Split(' '));
        else if (t < 0.6f) InputBridge.DebugPress(InputActionId.Interact, 100000);
        else if (t < 1.6f) Commands["aimat"](this, target.Split(' '));
        else { InputBridge.DebugRelease(InputActionId.Interact); _drag = null; File.AppendAllText(OutPath, "> drag released\n"); }
    }
    readonly StringBuilder _out = new();

    static readonly Dictionary<string, Func<DevConsole, string[], string>> Commands = new()
    {
        ["help"] = (c, a) => string.Join(" ", Commands.Keys),
        ["scenes"] = (c, a) => Scenes(),
        ["dump"] = (c, a) => Dump(a.Length > 0 ? int.Parse(a[0]) : 64, a.Length > 1 ? a[1] : null),
        ["cams"] = (c, a) => Cams(),
        ["canvases"] = (c, a) => Canvases(),
        ["find"] = (c, a) => Find(string.Join(" ", a)),
        ["comp"] = (c, a) => Comp(string.Join(" ", a)),
        ["shot"] = (c, a) => Shot(a.Length > 0 ? a[0] : "shot"),
        ["click"] = (c, a) => Click(string.Join(" ", a)),
        ["active"] = (c, a) => SetActive(a[0] == "1", string.Join(" ", a.Skip(1))),
        ["vmouse"] = (c, a) => { VirtualMouse.SetPosition(new Vector2(float.Parse(a[0]), float.Parse(a[1]))); return "ok"; },
        ["vimmediate"] = (c, a) => { VirtualMouse.UseImmediateChange = a[0] == "1"; return "ok"; },
        ["vbutton"] = (c, a) => { VirtualMouse.SetButton(int.Parse(a[0]), a[1] == "1"); return "ok"; },
        ["mice"] = (c, a) => string.Join("\n", UnityEngine.InputSystem.InputSystem.devices.ToArray().Select(d => $"{d.deviceId} {d.name} {d.layout} current={(UnityEngine.InputSystem.Mouse.current == d)} pos={(d.TryCast<UnityEngine.InputSystem.Mouse>() is var m && m != null ? m.position.ReadValue().ToString() : "")}")),
        ["inputjson"] = (c, a) => { var src = UnityEngine.Object.FindObjectOfType<BasicInputSource>(); var f = Path.Combine(Paths.BepInExRootPath, "shroomvr_inputactions.json"); File.WriteAllText(f, src._actions.ToJson()); return "wrote " + f; },
        ["ui"] = (c, a) => {
            var sb = new StringBuilder();
            sb.AppendLine($"GameInput ptr={GameInput.PointerPosition} has={GameInput.HasPointer} pad={GameInput.IsUsingGamepad} confirm={GameInput.ConfirmHeld} interact={GameInput.InteractDown}");
            var mc = UnityEngine.InputSystem.Mouse.current;
            sb.AppendLine($"Mouse.current={mc?.name} pos={mc?.position.ReadValue()} pressed={mc?.leftButton.isPressed} vm={VirtualMouse.Position} vmDown={VirtualMouse.LeftDown} legacyMouse={UnityEngine.Input.mousePosition} legacyBtn={UnityEngine.Input.GetMouseButton(0)}");
            var es0 = UnityEngine.EventSystems.EventSystem.current;
            var mod = es0 ? es0.currentInputModule?.TryCast<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() : null;
            if (mod != null) sb.AppendLine($"module point={mod.point?.action?.ReadValue<Vector2>()} click={mod.leftClick?.action?.IsPressed()} clickEnabled={mod.leftClick?.action?.enabled} over={mod.IsPointerOverGameObject(-1)}");
            var es = UnityEngine.EventSystems.EventSystem.current;
            sb.AppendLine($"EventSystem={(es ? DevConsole.PathOf(es.transform) : "-")} module={(es && es.currentInputModule ? es.currentInputModule.GetIl2CppType().FullName : "-")} selected={(es && es.currentSelectedGameObject ? es.currentSelectedGameObject.name : "-")}");
            if (es) {
                var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = VirtualMouse.Position };
                var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(ped, results);
                for (int i = 0; i < results.Count && i < 8; i++) { var r = results[i]; sb.AppendLine($"  hit {(r.gameObject ? DevConsole.PathOf(r.gameObject.transform) : "?")} module={(r.module ? r.module.GetIl2CppType().Name : "-")} dist={r.distance}"); }
            }
            foreach (var m in UnityEngine.Object.FindObjectsOfType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>())
                sb.AppendLine($"UIModule {DevConsole.PathOf(m.transform)} enabled={m.enabled} asset={(m.actionsAsset ? m.actionsAsset.name : "-")} pointerBehavior={m.pointerBehavior} over={m.IsPointerOverGameObject(-1)}");
            return sb.ToString(); },
        ["xrprobe"] = (c, a) => {
            var sb = new StringBuilder();
            var all = UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_IntegratedDescriptors;
            sb.AppendLine($"integrated={all?.Count} standalone={UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_StandaloneDescriptors?.Count} deprecated={UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_DeprecatedDescriptors?.Count} subsystems={UnityEngine.SubsystemManager.s_IntegratedSubsystems?.Count}");
            if (a.Length > 0 && a[0] == "map") { UnityEngine.SubsystemManager.StaticConstructScriptingClassMap(); sb.AppendLine("called StaticConstructScriptingClassMap"); }
            if (a.Length > 0 && a[0] == "reload") { UnityEngine.SubsystemManager.ReloadSubsystemsStarted(); UnityEngine.SubsystemManager.ReloadSubsystemsCompleted(); sb.AppendLine("called Reload"); }
            all = UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_IntegratedDescriptors;
            sb.AppendLine($"after: integrated={all?.Count}");
            for (int i = 0; all != null && i < all.Count; i++) sb.AppendLine($"  {all[i].id} {all[i].GetIl2CppType().FullName}");
            return sb.ToString(); },
        ["xrstat"] = (c, a) => {
            var sb = new StringBuilder();
            sb.AppendLine($"VR={VRSystem.Enabled} active={UnityEngine.XR.XRSettings.isDeviceActive} device={UnityEngine.XR.XRSettings.loadedDeviceName} eye={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight} mode={UnityEngine.XR.XRSettings.stereoRenderingMode} display.running={VRSystem.Display?.running}");
            sb.AppendLine($"cams={string.Join(", ", Cameras.Enabled().Select(x => x.name + ":" + x.stereoTargetEye + (x.targetTexture ? "(RT)" : "")))}");
            sb.AppendLine($"fps={1f / Time.unscaledDeltaTime:F1} frame={Time.frameCount} screen={Screen.width}x{Screen.height} {Screen.fullScreenMode}");
            return sb.ToString(); },
        ["vrshot"] = (c, a) => {
            var dir = Path.Combine(Paths.BepInExRootPath, "vrshots");
            Directory.CreateDirectory(dir);
            var name = a.Length > 0 ? a[0] : "shot";
            uint handle = 0;
            var err = Valve.VR.OpenVR.Screenshots.TakeStereoScreenshot(ref handle, Path.Combine(dir, name + "_preview"), Path.Combine(dir, name + "_vr"));
            return $"screenshot {err} handle={handle} -> {dir}"; },
        ["ovr"] = (c, a) => {
            Plugin.Logger.LogInfo("ovr test " + a[0]);
            switch (a[0]) {
                case "sysget": return "system=" + (Valve.VR.OpenVR.System != null);
                case "compget": return "compositor=" + (Valve.VR.OpenVR.Compositor != null);
                case "eye": { var m = Valve.VR.OpenVR.System.GetEyeToHeadTransform(Valve.VR.EVREye.Eye_Left); return $"eye {m.m3}"; }
                case "space": return "space=" + Valve.VR.OpenVR.Compositor.GetTrackingSpace();
                case "poses": { var r = new Valve.VR.TrackedDevicePose_t[64]; var g = new Valve.VR.TrackedDevicePose_t[64]; var e = Valve.VR.OpenVR.Compositor.GetLastPoses(r, g); return $"poses {e} valid={r[0].bPoseIsValid} y={r[0].mDeviceToAbsoluteTracking.m7}"; }
                case "devpose": { var r = new Valve.VR.TrackedDevicePose_t[64]; Valve.VR.OpenVR.System.GetDeviceToAbsoluteTrackingPose(Valve.VR.ETrackingUniverseOrigin.TrackingUniverseStanding, 0, r); return $"devpose valid={r[0].bPoseIsValid} y={r[0].mDeviceToAbsoluteTracking.m7}"; }
                case "screenshots": return "screenshots=" + (Valve.VR.OpenVR.Screenshots != null);
            }
            return "?"; },
        ["fov"] = (c, a) => { var cam = Camera.main; var before = cam.fieldOfView; cam.fieldOfView = float.Parse(a[0]); return $"{before} -> set {a[0]} -> {cam.fieldOfView} stereo={cam.stereoTargetEye} stereoEnabled={cam.stereoEnabled}"; },
        ["res"] = (c, a) => { if (a.Length >= 2) Screen.SetResolution(int.Parse(a[0]), int.Parse(a[1]), a.Length > 2 ? Enum.Parse<FullScreenMode>(a[2]) : FullScreenMode.Windowed); return $"screen={Screen.width}x{Screen.height} {Screen.fullScreenMode} eye={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}"; },
        ["hands"] = (c, a) => {
            var r = VRRig.Instance; if (r == null) return "no rig";
            var l = Laser.Instance;
            return $"L tracked={r.Left.Tracked} pos={r.Left.Grip.position} active={VRInput.IsActive(Hand.Left)} dev={VRInput.DeviceIndex(Hand.Left)}\n" +
                   $"R tracked={r.Right.Tracked} pos={r.Right.Grip.position} active={VRInput.IsActive(Hand.Right)} dev={VRInput.DeviceIndex(Hand.Right)}\n" +
                   $"head={VRRig.HeadPosition} laser hand={l?.ActiveHand} target={l?.HasTarget} kind={l?.Kind} screen={l?.ScreenPoint}";
        },
        ["point"] = (c, a) => { if (a.Length == 0 || a[0] == "off") { Laser.Instance.DebugScreen = null; return "laser"; } Laser.Instance.DebugScreen = new Vector2(float.Parse(a[0]), float.Parse(a[1])); return "ok"; },
        ["press"] = (c, a) => { var id = Enum.Parse<InputActionId>(a[0], true); InputBridge.DebugPress(id, a.Length > 1 ? int.Parse(a[1]) : 3); return "pressed " + id; },
        ["move"] = (c, a) => { InputBridge.DebugMove(new Vector2(0, float.Parse(a[0])), float.Parse(a[1])); return "moving"; },
        // Controller input as SteamVR would report it: stick <L|R> <x> <y> [seconds], vpress <VRAction> [L|R] [frames].
        ["stick"] = (c, a) => { VRInput.DebugStick(ParseHand(a[0]), new Vector2(float.Parse(a[1]), float.Parse(a[2])), a.Length > 3 ? float.Parse(a[3]) : 0.3f); return "stick " + a[0]; },
        ["focus"] = (c, a) => {
            if (!ControllerNav.S_ControllerFocus.TryGetFocus(out var f) || f == null) return "no focus service";
            var item = f.FocusedItem();
            var comp = item?.TryCast<Component>();
            var card = item?.TryCast<V_GameplayCardInstance>();
            var icon = card != null && card._iconRenderer != null && card._iconRenderer.sprite != null ? " card:" + card._iconRenderer.sprite.name.Replace("Icon_", "") : "";
            return $"mode={f.Mode} pad={f.UsingGamepad} nav={StickNav.Active} masked={StickNav.Masked} depth={f.StackDepth} blocked={f._blockedBy} " +
                   $"item={(item == null ? "-" : item.GetIl2CppType().Name + icon + (comp != null ? " " + PathOf(comp.transform) : ""))}"; },
        ["squeeze"] = (c, a) => { VRInput.DebugSqueeze(ParseHand(a[0]), float.Parse(a[1]), a.Length > 2 ? float.Parse(a[2]) : 0.3f); return "squeeze " + a[0] + " " + a[1]; },
        ["vpress"] = (c, a) => { var act = Enum.Parse<VRAction>(a[0], true); VRInput.DebugPress(act, a.Length > 1 ? ParseHand(a[1]) : Hand.Left, a.Length > 2 ? int.Parse(a[2]) : 3); return "pressed " + act; },
        ["recenter"] = (c, a) => { VRRig.Instance?.Recenter(); return "ok"; },
        ["vstate"] = (c, a) => { var d = VirtualMouse.Device; return d == null ? "none" : $"{d.name} id={d.deviceId} added={d.added} enabled={d.enabled} canRunInBackground={d.canRunInBackground} lastUpdate={d.lastUpdateTime} now={UnityEngine.InputSystem.LowLevel.InputState.currentTime} bg={UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior} focus={Application.isFocused}"; },
        ["vbytes"] = (c, a) => VirtualMouse.DebugBytes(),
        ["screenpos"] = (c, a) => {
            // Screen position (bottom-left origin) of every active object whose name contains the text.
            var needle = string.Join(" ", a); var cam = VRRig.ReferenceCamera ?? Camera.main; var sb = new StringBuilder();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>()) {
                if (t.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var rt = t.TryCast<RectTransform>(); var p = t.position;
                if (rt != null) { var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4); rt.GetWorldCorners(corners); p = (corners[0] + corners[2]) * 0.5f; }
                var sp = cam.WorldToScreenPoint(p); sb.AppendLine($"{sp.x:F0} {sp.y:F0} z={sp.z:F2} {PathOf(t)}");
            }
            return sb.ToString(); },
        ["fakeaim"] = (c, a) => {
            // fakeaim <yaw> <pitch>: right hand 25cm right/35cm below/30cm in front of the eye, aiming relative to the body.
            var r = VRRig.Instance; if (r == null) return "no rig";
            if (a.Length == 0 || a[0] == "off") { r.Right.DebugAim = null; return "off"; }
            var body = CameraLook.AnchorYaw(VRRig.ReferenceFlat);
            var pos = VRRig.HeadPosition + body * new Vector3(0.25f, -0.35f, 0.3f);
            var rot = body * Quaternion.Euler(float.Parse(a[1]), float.Parse(a[0]), 0);
            r.Right.SetDebugAimWorld(pos, rot);
            return "ok"; },
        ["aimat"] = (c, a) => {
            // Point the simulated right hand at the centre of the first active object whose name contains the text.
            var needle = string.Join(" ", a); var r = VRRig.Instance; if (r == null) return "no rig";
            // "card:Spade" = the card whose art is Icon_Spade; "~text" = path contains; otherwise path ends with.
            System.Collections.Generic.IEnumerable<Transform> candidates;
            if (needle.StartsWith("card:")) {
                var icon = needle.Substring(5);
                var exact = new List<Transform>(); var partial = new List<Transform>();
                foreach (var v in UnityEngine.Object.FindObjectsOfType<V_GameplayCardInstance>()) {
                    var ir = v._iconRenderer;
                    if (ir == null || ir.sprite == null) continue;
                    if (ir.sprite.name == "Icon_" + icon) exact.Add(v.transform); else if (ir.sprite.name.Contains(icon)) partial.Add(v.transform);
                }
                candidates = exact.Count > 0 ? exact : partial;
            } else if (needle.StartsWith("~")) {
                var sub = needle.Substring(1);
                candidates = UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => PathOf(t).Contains(sub));
            } else candidates = UnityEngine.Object.FindObjectsOfType<Transform>().Where(t => PathOf(t).EndsWith(needle));
            foreach (var t in candidates) {
                if (!t.gameObject.activeInHierarchy) continue;
                // Centre of everything visible under the object (the whole card, not its cost label).
                Bounds? b = null;
                foreach (var col in t.GetComponentsInChildren<Collider>()) { if (!col.enabled) continue; if (b == null) b = col.bounds; else { var bb = b.Value; bb.Encapsulate(col.bounds); b = bb; } }
                if (b == null) foreach (var rend in t.GetComponentsInChildren<Renderer>()) { if (!rend.isVisible) continue; if (b == null) b = rend.bounds; else { var bb = b.Value; bb.Encapsulate(rend.bounds); b = bb; } }
                Vector3 p;
                if (b != null) p = b.Value.center;
                else { var rt = t.TryCast<RectTransform>(); if (rt == null) continue; var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4); rt.GetWorldCorners(corners); p = (corners[0] + corners[2]) * 0.5f; }
                p = CardHand.ToShown(t, p); // cards held in the off hand are seen (and pointed at) there
                var body = CameraLook.AnchorYaw(VRRig.ReferenceFlat);
                var origin = VRRig.ViewRelative.MultiplyPoint3x4(VRRig.HeadPosition + body * new Vector3(0.25f, -0.35f, 0.3f));
                r.Right.SetDebugAimWorld(origin, Quaternion.LookRotation(p - origin));
                return $"aiming at {PathOf(t)}\n  target={p} origin={origin} dir={(p - origin).normalized}";
            }
            return "not found"; },
        ["colliders"] = (c, a) => {
            // Collider types/layers under every active object whose path contains the text.
            var needle = string.Join(" ", a); var sb = new StringBuilder(); var seen = new HashSet<string>();
            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>()) { var p = PathOf(col.transform); if (p.Contains(needle) && seen.Add(TypeName(col) + col.gameObject.layer + col.isTrigger)) sb.AppendLine($"3D {TypeName(col)} L{col.gameObject.layer} trigger={col.isTrigger} {p}"); }
            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider2D>()) { var p = PathOf(col.transform); if (p.Contains(needle) && seen.Add(TypeName(col) + col.gameObject.layer + col.isTrigger)) sb.AppendLine($"2D {TypeName(col)} L{col.gameObject.layer} trigger={col.isTrigger} {p}"); }
            foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.BaseRaycaster>()) sb.AppendLine($"raycaster {TypeName(r)} on {PathOf(r.transform)} enabled={r.enabled}" + (r.TryCast<UnityEngine.EventSystems.PhysicsRaycaster>() is var pr && pr != null ? $" mask={LayerNames(pr.eventMask)}" : ""));
            return sb.ToString(); },
        ["colbounds"] = (c, a) => { var go = FindPath(string.Join(" ", a)); if (go == null) return "not found"; var sb = new StringBuilder(); foreach (var col in go.GetComponentsInChildren<Collider>(true)) sb.AppendLine($"{PathOf(col.transform).Split('/').Last()} L{col.gameObject.layer} en={col.enabled} active={col.gameObject.activeInHierarchy} center={col.bounds.center} size={col.bounds.size}"); return sb.ToString(); },
        ["targets"] = (c, a) => {
            // Every collider on the SpellTargets layer (what cards are played onto), with its screen position.
            var cam = VRRig.ReferenceCamera ?? Camera.main; var sb = new StringBuilder();
            foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>()) {
                if (col.gameObject.layer != 8 || !col.enabled) continue;
                var sp = cam.WorldToScreenPoint(col.bounds.center);
                sb.AppendLine($"{sp.x:F0} {sp.y:F0} z={sp.z:F1} {PathOf(col.transform).Replace("GeneratedTunnel/Encounters/", "")}");
            }
            return sb.ToString(); },
        ["release"] = (c, a) => { InputBridge.DebugRelease(InputActionId.Interact); return "released"; },
        ["hold"] = (c, a) => { InputBridge.DebugPress(InputActionId.Interact, 100000); return "holding"; },
        ["handicons"] = (c, a) => {
            var names = new List<string>();
            foreach (var v in UnityEngine.Object.FindObjectsOfType<V_GameplayCardInstance>())
                if (PathOf(v.transform).Contains("Hand]") && v._iconRenderer != null && v._iconRenderer.sprite != null) names.Add(v._iconRenderer.sprite.name.Replace("Icon_", ""));
            names.Sort(); return string.Join(", ", names); },
        ["drag"] = (c, a) => {
            // drag <card path suffix> | <target path suffix>: a real laser drag with the trigger held.
            var parts = string.Join(" ", a).Split('|');
            c._drag = (parts[0].Trim(), parts[1].Trim(), Time.unscaledTime);
            return "dragging"; },
        ["rayprobe"] = (c, a) => {
            var r = VRRig.Instance; if (r == null) return "no rig";
            var aim = r.Right.Aim; var sb = new StringBuilder($"aim pos={aim.position} fwd={aim.forward}\n");
            foreach (var h in Physics.RaycastAll(new Ray(aim.position, aim.forward), 10f, ~0, QueryTriggerInteraction.Collide))
                sb.AppendLine($"  3D {h.distance:F2} L{h.collider.gameObject.layer} {PathOf(h.collider.transform).Split('/').Last()}");
            var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit>(16);
            int n = Physics.RaycastNonAlloc(new Ray(aim.position, aim.forward), buf, 10f, ~0, QueryTriggerInteraction.Collide);
            sb.AppendLine($"  NonAlloc count={n} first={(n > 0 && buf[0].collider != null ? buf[0].collider.name : "-")}");
            return sb.ToString(); },
        ["fakeleft"] = (c, a) => {
            // Simulated left hand held up in front-left of the face (for map-in-hand checks).
            var r = VRRig.Instance; if (r == null) return "no rig";
            if (a.Length > 0 && a[0] == "off") { r.Left.DebugAim = null; return "off"; }
            // fakeleft [dx dy dz [pitch yaw roll]]: body-relative offset from the head (default: held low front-left).
            var body = CameraLook.AnchorYaw(VRRig.ReferenceFlat);
            float F(int i, float d) => a.Length > i ? float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture) : d;
            var pos = VRRig.HeadPosition + body * new Vector3(F(0, -0.18f), F(1, -0.25f), F(2, 0.35f));
            r.Left.SetDebugAimWorld(pos, body * Quaternion.Euler(F(3, -40), F(4, 20), F(5, 0)));
            return "left hand at " + pos; },
        ["cardhand"] = (c, a) => CardHand.Describe(),
        ["rendername"] = (c, a) => {
            // rendername <object name> <0|1>: enable/disable every renderer on objects with exactly that name.
            int n = 0; bool on = a[a.Length - 1] == "1"; var name = string.Join(" ", a.Take(a.Length - 1));
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>()) if (r.name == name) { r.enabled = on; n++; }
            return $"{n} renderers {(on ? "on" : "off")}"; },
        ["camprop"] = (c, a) => {
            // camprop <camera name> <near|far> <value>
            var camName = string.Join(" ", a.Take(a.Length - 2)); a = a.Skip(a.Length - 3).ToArray();
            foreach (var cam in Cameras.Enabled()) if (cam.name == camName) {
                float v = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                if (a[1] == "near") cam.nearClipPlane = v; else cam.farClipPlane = v;
                return $"{cam.name} near={cam.nearClipPlane} far={cam.farClipPlane}"; }
            return "no camera"; },
        ["ppv"] = (c, a) => {
            // Post-processing: every volume's effects, and every layer's camera and volume mask.
            var sb = new StringBuilder();
            foreach (var v in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.PostProcessing.PostProcessVolume>()) {
                var prof = v.sharedProfile; sb.AppendLine($"volume {PathOf(v.transform)} L{v.gameObject.layer} global={v.isGlobal} weight={v.weight} profile={(prof ? prof.name : "-")} en={v.isActiveAndEnabled}");
                if (prof != null) foreach (var e in prof.settings) sb.AppendLine($"    {e.GetIl2CppType().FullName} active={e.active} enabled={e.enabled.value}");
            }
            foreach (var l in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.PostProcessing.PostProcessLayer>())
                sb.AppendLine($"layer on {l.name} en={l.enabled} mask={l.volumeLayer.value} trigger={(l.volumeTrigger ? l.volumeTrigger.name : "-")} aa={l.antialiasingMode}");
            return sb.ToString(); },
        ["behaviour"] = (c, a) => {
            // behaviour <type name> <0|1> <path suffix>: enable/disable that component on the object.
            var go = UnityEngine.Object.FindObjectsOfType<Transform>().FirstOrDefault(t => PathOf(t).EndsWith(string.Join(" ", a.Skip(2))));
            if (go == null) return "not found";
            foreach (var b in go.GetComponents<Behaviour>()) if (b.GetIl2CppType().Name == a[0] || b.GetIl2CppType().FullName == a[0]) { b.enabled = a[1] == "1"; return $"{b.GetIl2CppType().FullName} enabled={b.enabled}"; }
            return "no such component"; },
        ["lights"] = (c, a) => {
            // Every enabled light: type, position, range, intensity, mask, and distance to the main camera.
            var cam = VRRig.ReferenceCamera ?? Camera.main; var sb = new StringBuilder();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) {
                if (!l.isActiveAndEnabled) continue;
                sb.AppendLine($"{PathOf(l.transform).Split('/').Last()} {l.type} pos={l.transform.position} d={(cam ? Vector3.Distance(cam.transform.position, l.transform.position) : -1):F1} range={l.range:F1} int={l.intensity:F2} mask={l.cullingMask} mode={l.renderMode}");
            }
            sb.AppendLine($"pixelLights={QualitySettings.pixelLightCount} path={(cam ? cam.actualRenderingPath.ToString() : "-")}");
            return sb.ToString(); },
        ["matprops"] = (c, a) => {
            // matprops <renderer path suffix>: every shader property of its material, with the global value of the same name.
            var needle = string.Join(" ", a); Renderer rend = null;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>()) if (PathOf(r.transform).EndsWith(needle) && r.gameObject.activeInHierarchy) { rend = r; break; }
            if (rend == null) return "not found";
            var m = rend.sharedMaterial; var sh = m.shader; var sb = new StringBuilder($"{PathOf(rend.transform)} shader={sh.name} kw={string.Join(",", m.shaderKeywords)}\n");
            var mpb = new MaterialPropertyBlock(); rend.GetPropertyBlock(mpb);
            for (int i = 0; i < sh.GetPropertyCount(); i++) {
                var n = sh.GetPropertyName(i); var t = sh.GetPropertyType(i);
                string v = t switch {
                    UnityEngine.Rendering.ShaderPropertyType.Color => m.GetColor(n) + " global=" + Shader.GetGlobalColor(n),
                    UnityEngine.Rendering.ShaderPropertyType.Vector => m.GetVector(n) + " global=" + Shader.GetGlobalVector(n),
                    UnityEngine.Rendering.ShaderPropertyType.Texture => (m.GetTexture(n) ? m.GetTexture(n).name : "-") + " global=" + (Shader.GetGlobalTexture(n) ? Shader.GetGlobalTexture(n).name : "-") + (mpb.HasTexture(n) && mpb.GetTexture(n) ? " block=" + mpb.GetTexture(n).name : ""),
                    _ => m.GetFloat(n) + " global=" + Shader.GetGlobalFloat(n) + (mpb.HasFloat(n) ? " block=" + mpb.GetFloat(n) : ""),
                };
                sb.AppendLine($"  {n} ({t}) = {v}");
            }
            return sb.ToString(); },
        ["set"] = (c, a) => {
            // set <config key> <value>: change a ShroomVR setting at runtime (e.g. set CardsInHand false).
            foreach (var kv in VRConfig.File) if (kv.Key.Key == a[0]) { kv.Value.SetSerializedValue(a[1]); return $"{kv.Key} = {kv.Value.BoxedValue}"; }
            return "no such setting"; },
        ["cardtext"] = (c, a) => {
            // Text of every card in a hand (or whose path contains the argument): name, cost, rules.
            var needle = a.Length > 0 ? string.Join(" ", a) : "Hand]"; var sb = new StringBuilder();
            foreach (var v in UnityEngine.Object.FindObjectsOfType<V_GameplayCardInstance>()) {
                var path = PathOf(v.transform); if (!path.Contains(needle)) continue;
                var texts = v.GetComponentsInChildren<TMPro.TMP_Text>().Where(t => t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text)).Select(t => t.text.Replace("\n", " ").Trim());
                sb.AppendLine($"{(v._iconRenderer?.sprite != null ? v._iconRenderer.sprite.name.Replace("Icon_", "") : "?")}: {string.Join(" | ", texts)}");
            }
            return sb.ToString(); },
        ["lift"] = (c, a) => { var v = UnityEngine.Object.FindObjectOfType<PlayerCamView>(); return v == null ? "-" : $"frame={v._lookFrame} rootY={v.transform.parent.position.y:F2} viewY={v.transform.localPosition.y:F2} lift={CameraLook.RoomLift:F2} camY={VRRig.ReferenceCamera?.transform.position.y:F2}"; },
        ["laser"] = (c, a) => { var l = Laser.Instance; return $"hand={l.ActiveHand} target={l.HasTarget} kind={l.Kind} screen={l.ScreenPoint} mouse={VirtualMouse.Position} cost={l.AverageMs:F2}ms"; },
        ["fixhead"] = (c, a) => { VRRig.DebugFixedHead = a.Length == 0 || a[0] != "off"; VRRig.DebugHeadYaw = a.Length > 0 && float.TryParse(a[0], out var y) ? y : 0; return $"fixed head {VRRig.DebugFixedHead} yaw {VRRig.DebugHeadYaw}"; },
        ["mirror"] = (c, a) => { VRSystem.SetMirror(int.Parse(a[0])); return "mirror " + a[0]; },
        ["quit"] = (c, a) => { Application.Quit(); return "quitting"; },
        ["mats"] = (c, a) => {
            // Renderers under the first active object whose path contains the text: shader, textures, sorting.
            var needle = string.Join(" ", a); Transform root = null;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>()) if (PathOf(t).EndsWith(needle)) { root = t; break; }
            if (root == null) return "not found";
            var sb = new StringBuilder(PathOf(root) + "\n");
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) {
                var m = r.sharedMaterial;
                var tex = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                var sr = r.TryCast<SpriteRenderer>();
                sb.AppendLine($"  {(r.gameObject.activeInHierarchy ? "" : "[off] ")}{r.name} L{r.gameObject.layer} {TypeName(r).Replace("UnityEngine.", "")} en={r.enabled} vis={r.isVisible} shader={(m ? m.shader.name : "-")} q={(m ? m.renderQueue : 0)} tex={(tex ? tex.name + " " + tex.GetIl2CppType().Name : "-")} sprite={(sr != null && sr.sprite ? sr.sprite.name : "-")} sort={r.sortingLayerName}/{r.sortingOrder} kw={(m ? string.Join(",", m.shaderKeywords) : "")}");
            }
            return sb.ToString(); },
        ["cardart"] = (c, a) => {
            var sb = new StringBuilder();
            foreach (var v in UnityEngine.Object.FindObjectsOfType<V_GameplayCardInstance>()) {
                var ir = v._iconRenderer; var ci = v._customIconInstance; var root = v._iconRoot;
                sb.AppendLine($"{PathOf(v.transform).Replace("GeneratedTunnel/Encounters/", "")}\n   icon={(ir ? (ir.sprite ? ir.sprite.name : "NULL") + " en=" + ir.enabled + " active=" + ir.gameObject.activeInHierarchy : "-")} custom={(ci ? ci.name + " active=" + ci.activeInHierarchy + " parent=" + (ci.transform.parent ? ci.transform.parent.name : "-") : "none")} iconRoot={(root ? root.name + " children=" + root.childCount : "-")}");
                if (root) for (int i = 0; i < root.childCount; i++) { var ch = root.GetChild(i); sb.AppendLine($"     child {ch.name} active={ch.gameObject.activeInHierarchy} renderers={ch.GetComponentsInChildren<Renderer>(true).Length}"); }
            }
            return sb.ToString(); },
        ["icondiag"] = (c, a) => {
            // Systemic view: every card data asset in memory, and whether icon sprites are loaded at all.
            int total = 0, broken = 0, unset = 0; var ids = new Dictionary<int, int>(); var sb = new StringBuilder();
            foreach (var d in Resources.FindObjectsOfTypeAll<GameplayCardData>()) {
                total++; var r = d.Icon;
                if (!r.isSet) unset++; else if (r.isBroken) broken++;
                ids[r.instanceID] = ids.TryGetValue(r.instanceID, out var k) ? k + 1 : 1;
                if (total <= 3) sb.AppendLine($"  {d.name} dataId={d.GetInstanceID()} iconId={r.instanceID} broken={r.isBroken}");
            }
            int origTotal = 0, origBroken = 0;
            foreach (var d in Resources.FindObjectsOfTypeAll<GameplayCardData>()) {
                if (d.name.EndsWith("(Clone)")) continue;
                origTotal++; if (d.Icon.isBroken) origBroken++;
                if (origTotal <= 2) sb.AppendLine($"  original {d.name} dataId={d.GetInstanceID()} iconId={d.Icon.instanceID} broken={d.Icon.isBroken} asset={(d.Icon.isBroken ? "-" : d.Icon.asset?.name)}");
            }
            sb.AppendLine($"originals: {origTotal} broken={origBroken}");
            int sprites = 0; foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>()) if (sp.name.StartsWith("Icon_")) sprites++;
            sb.Insert(0, $"card data: {total} unset={unset} broken={broken} distinctIconIds={ids.Count} top={string.Join(",", ids.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key + "x" + x.Value))}; Icon_ sprites loaded={sprites}\n");
            return sb.ToString(); },
        ["patches"] = (c, a) => string.Join("\n", Plugin.Harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name)),
        ["unpatch"] = (c, a) => {
            int n = 0;
            foreach (var m in Plugin.Harmony.GetPatchedMethods().ToList())
                if ((m.DeclaringType?.Name + "." + m.Name).Contains(a[0])) { Plugin.Harmony.Unpatch(m, HarmonyLib.HarmonyPatchType.All, Plugin.Guid); n++; }
            return $"unpatched {n}"; },
        ["vdevice"] = (c, a) => { VirtualMouse.UseOwnDevice = a[0] == "own"; VirtualMouse.Reset(); return "ok"; },
        ["depths"] = (c, a) => {
            // Distance from the flat reference camera to visible renderers on the UI/card layers.
            var cam = VRRig.ReferenceCamera ?? Camera.main; var sb = new StringBuilder();
            var seen = new Dictionary<string, (int n, float min, float max)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>()) {
                if (!r.isVisible || ((1 << r.gameObject.layer) & ((1 << 5) | (1 << 7) | (1 << 9))) == 0) continue;
                var d = cam.transform.InverseTransformPoint(r.bounds.center).z;
                var key = $"L{r.gameObject.layer} {r.transform.root.name}/{(r.transform.parent ? r.transform.parent.name : "")}";
                seen[key] = seen.TryGetValue(key, out var v) ? (v.n + 1, Mathf.Min(v.min, d), Mathf.Max(v.max, d)) : (1, d, d);
            }
            foreach (var kv in seen.OrderBy(k => k.Value.min)) sb.AppendLine($"{kv.Value.min:F2}-{kv.Value.max:F2}m x{kv.Value.n} {kv.Key}");
            foreach (var p in VRUI.Planes) sb.AppendLine($"canvas {p.Canvas.name} at {cam.transform.InverseTransformPoint(p.Center).z:F2}m");
            return sb.ToString(); },
        ["timescale"] = (c, a) => { Time.timeScale = float.Parse(a[0]); return "ok"; },
    };

    /// <summary>Lets other systems add console commands.</summary>
    public static void Register(string name, Func<string[], string> fn) => Commands[name] = (c, a) => fn(a);

    void Update()
    {
        if (!VRSystem.Enabled) VirtualMouse.Flush(); // in VR the frame tick flushes it
        StepDrag();
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 0.25f;
        try
        {
            if (!File.Exists(CmdPath)) return;
            var lines = File.ReadAllLines(CmdPath);
            File.Delete(CmdPath);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                _out.Clear();
                _out.AppendLine($"> {line}");
                try
                {
                    _out.AppendLine(Commands.TryGetValue(parts[0], out var fn) ? fn(this, parts.Skip(1).ToArray()) : "unknown command");
                }
                catch (Exception e)
                {
                    _out.AppendLine("ERROR " + e);
                }
                File.AppendAllText(OutPath, _out.ToString());
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("DevConsole: " + e);
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    public static IEnumerable<GameObject> AllRoots()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!s.isLoaded) continue;
            foreach (var go in s.GetRootGameObjects()) yield return go;
        }
        foreach (var go in DontDestroyRoots()) yield return go;
    }

    static GameObject _ddolProbe;

    public static IEnumerable<GameObject> DontDestroyRoots()
    {
        if (_ddolProbe == null)
        {
            _ddolProbe = new GameObject("ShroomVR_DDOLProbe");
            DontDestroyOnLoad(_ddolProbe);
            _ddolProbe.hideFlags = HideFlags.HideAndDontSave;
        }
        return _ddolProbe.scene.GetRootGameObjects().Where(g => g != _ddolProbe);
    }

    public static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while ((t = t.parent) != null) sb.Insert(0, t.name + "/");
        return sb.ToString();
    }

    public static GameObject FindPath(string path)
    {
        foreach (var root in AllRoots())
        {
            if (path == root.name) return root.gameObject;
            if (!path.StartsWith(root.name + "/")) continue;
            var t = root.transform.Find(path.Substring(root.name.Length + 1));
            if (t != null) return t.gameObject;
        }
        return null;
    }

    public static string TypeName(Il2CppSystem.Object o) => o.GetIl2CppType().FullName;

    static string Scenes()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            sb.AppendLine($"{i}: {s.name} loaded={s.isLoaded} roots={s.rootCount} active={(SceneManager.GetActiveScene() == s)}");
        }
        sb.AppendLine("DDOL roots: " + string.Join(", ", DontDestroyRoots().Select(g => g.name)));
        return sb.ToString();
    }

    static string Dump(int maxDepth, string filter)
    {
        var sb = new StringBuilder();
        foreach (var root in AllRoots())
        {
            if (filter != null && !root.name.Contains(filter)) continue;
            sb.AppendLine($"## scene={root.scene.name}");
            DumpRec(root.transform, 0, maxDepth, sb);
        }
        var file = Path.Combine(Paths.BepInExRootPath, "shroomvr_dump.txt");
        File.WriteAllText(file, sb.ToString());
        return $"wrote {file} ({sb.Length} chars)";
    }

    static void DumpRec(Transform t, int depth, int maxDepth, StringBuilder sb)
    {
        var go = t.gameObject;
        var comps = go.GetComponents<Component>()
            .Where(c => c != null && !(c.TryCast<Transform>() != null && c.TryCast<RectTransform>() == null))
            .Select(c => TypeName(c).Replace("UnityEngine.", "U.")).ToArray();
        sb.Append(' ', depth * 2)
          .Append(go.activeSelf ? "" : "[off] ")
          .Append(go.name)
          .Append($" L{go.layer}")
          .Append(comps.Length > 0 ? " {" + string.Join(", ", comps) + "}" : "")
          .AppendLine();
        if (depth >= maxDepth) return;
        for (int i = 0; i < t.childCount; i++) DumpRec(t.GetChild(i), depth + 1, maxDepth, sb);
    }

    static string Cams()
    {
        var sb = new StringBuilder();
        foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (cam.gameObject.scene.name == null) continue; // prefab assets
            sb.AppendLine($"{PathOf(cam.transform)} en={cam.enabled}/{cam.gameObject.activeInHierarchy} depth={cam.depth} " +
                          $"mask={LayerNames(cam.cullingMask)} clear={cam.clearFlags} fov={cam.fieldOfView:F1} ortho={cam.orthographic} " +
                          $"near={cam.nearClipPlane} far={cam.farClipPlane} rt={(cam.targetTexture ? cam.targetTexture.name : "-")} " +
                          $"pos={cam.transform.position} rot={cam.transform.eulerAngles} stereo={cam.stereoTargetEye} main={(Camera.main == cam)}");
        }
        return sb.ToString();
    }

    public static string LayerNames(int mask)
    {
        if (mask == -1) return "Everything";
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) names.Add($"{i}:{LayerMask.LayerToName(i)}");
        return "[" + string.Join(",", names) + "]";
    }

    static string Canvases()
    {
        var sb = new StringBuilder();
        foreach (var cv in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (cv.gameObject.scene.name == null || (cv.transform.parent != null && cv.transform.parent.GetComponentInParent<Canvas>(true) != null)) continue;
            var scaler = cv.GetComponent<CanvasScaler>();
            sb.AppendLine($"{PathOf(cv.transform)} active={cv.gameObject.activeInHierarchy} mode={cv.renderMode} cam={(cv.worldCamera ? cv.worldCamera.name : "-")} " +
                          $"plane={cv.planeDistance} order={cv.sortingOrder} layer={cv.gameObject.layer} size={cv.GetComponent<RectTransform>().sizeDelta} " +
                          $"scaler={(scaler ? scaler.uiScaleMode + " ref=" + scaler.referenceResolution + " match=" + scaler.matchWidthOrHeight : "-")}");
        }
        return sb.ToString();
    }

    static Hand ParseHand(string s) => s.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? Hand.Right : Hand.Left;

    static string Find(string needle)
    {
        var sb = new StringBuilder();
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t.gameObject.scene.name == null) continue;
            if (t.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                sb.AppendLine($"{(t.gameObject.activeInHierarchy ? "" : "[off] ")}{PathOf(t)}");
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, Type> TypeCache = new();

    /// <summary>Maps an IL2CPP type name to its generated interop wrapper type.</summary>
    public static Type ManagedType(string il2cppFullName)
    {
        if (TypeCache.TryGetValue(il2cppFullName, out var t)) return t;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(il2cppFullName) ?? asm.GetType("Il2Cpp" + il2cppFullName);
            if (t != null && typeof(Il2CppObjectBase).IsAssignableFrom(t)) break;
            t = null;
        }
        return TypeCache[il2cppFullName] = t;
    }

    static string Comp(string path)
    {
        var go = FindPath(path);
        if (go == null) return "not found";
        var sb = new StringBuilder();
        sb.AppendLine($"{PathOf(go.transform)} active={go.activeInHierarchy} layer={go.layer} pos={go.transform.position} lpos={go.transform.localPosition} lrot={go.transform.localEulerAngles} lscale={go.transform.localScale}");
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            var name = TypeName(c);
            sb.AppendLine("  [" + name + "]");
            var mt = ManagedType(name);
            if (mt == null) continue;
            object wrapped;
            try { wrapped = Activator.CreateInstance(mt, c.Pointer); } catch { continue; }
            foreach (var p in mt.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                string val;
                try { val = Fmt(p.GetValue(wrapped)); } catch (Exception e) { val = "<" + (e.InnerException ?? e).GetType().Name + ">"; }
                sb.AppendLine($"    {p.Name} = {val}");
            }
        }
        return sb.ToString();
    }

    static string Fmt(object v) => v switch
    {
        null => "null",
        UnityEngine.Object uo => uo == null ? "null(destroyed)" : $"{uo.name} ({TypeName(uo)})",
        Il2CppSystem.Object ob => TypeName(ob),
        _ => v.ToString(),
    };

    static string Shot(string name)
    {
        var file = Path.Combine(Paths.BepInExRootPath, name + ".png");
        if (VRSystem.Enabled) ScreenCapture.CaptureScreenshot(file, ScreenCapture.StereoScreenCaptureMode.LeftEye);
        else ScreenCapture.CaptureScreenshot(file);
        return "capturing " + file;
    }

    static string Click(string path)
    {
        var go = FindPath(path);
        if (go == null) return "not found";
        var b = go.GetComponent<Button>();
        if (b == null) return "no Button";
        b.onClick.Invoke();
        return "clicked";
    }

    static string SetActive(bool on, string path)
    {
        var go = FindPath(path);
        if (go == null) return "not found";
        go.SetActive(on);
        return "ok";
    }
}
