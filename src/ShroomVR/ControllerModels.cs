using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Valve.VR;

namespace ShroomVR;

/// <summary>
/// Shows the SteamVR render model of a tracked controller. Component models (buttons, trigger,
/// thumbstick) are animated from the live component state. Loading is fully asynchronous and is
/// polled from Update. The object should sit under a transform that follows the device's raw pose.
/// </summary>
public class ControllerModel : MonoBehaviour
{
    public ControllerModel(IntPtr ptr) : base(ptr) { }

    /// <summary>Tint applied to every controller material. Multiplied by <see cref="Brightness"/>.</summary>
    public static Color Tint = Color.white;

    /// <summary>Brightness multiplier, since the game's tunnels are very dark.</summary>
    public static float Brightness = 1f;

    static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        ClassInjector.RegisterTypeInIl2Cpp<ControllerModel>();
        _registered = true;
    }

    /// <summary>
    /// Creates a controller model under <paramref name="parent"/>. <paramref name="deviceIndexProvider"/>
    /// returns the OpenVR device index (or k_unTrackedDeviceIndexInvalid). The optional
    /// <paramref name="devicePathProvider"/> returns the input source handle (e.g. for
    /// /user/hand/left). When given, component animation uses the SteamVR Input state; otherwise
    /// it uses the legacy controller state.
    /// </summary>
    public static ControllerModel Create(Transform parent, int layer, Func<uint> deviceIndexProvider, Func<ulong> devicePathProvider = null)
    {
        Register();
        var go = new GameObject("ShroomVR ControllerModel");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        var model = go.AddComponent<ControllerModel>();
        model._layer = layer;
        model._deviceIndexProvider = deviceIndexProvider;
        model._devicePathProvider = devicePathProvider;
        return model;
    }

    // ---- per-instance state ----------------------------------------------------------------

    class Part
    {
        public string ComponentName; // null for the single full model
        public string ModelName;
        public GameObject Go;
        public bool Built;
    }

    int _layer;
    Func<uint> _deviceIndexProvider;
    Func<ulong> _devicePathProvider;
    uint _deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
    string _renderModelName;
    readonly List<Part> _parts = new();
    GameObject _fallback;
    float _nextRetry;
    bool _loggedFailure;

    /// <summary>The render model currently shown, or null.</summary>
    public string RenderModelName => _renderModelName;

    /// <summary>True when the real render model (not the fallback) is fully built.</summary>
    public bool IsLoaded => _parts.Count > 0 && _parts.TrueForAll(p => p.Built) && _parts.Exists(p => p.Go != null);

    void Update()
    {
        try
        {
            Tick();
        }
        catch (Exception e)
        {
            if (!_loggedFailure) Plugin.Logger.LogError("ControllerModel: " + e);
            _loggedFailure = true;
            ShowFallback(true);
        }
    }

    void Tick()
    {
        var system = OpenVR.System;
        var models = OpenVR.RenderModels;
        if (system == null || models == null)
        {
            ShowFallback(true);
            return;
        }

        uint index = _deviceIndexProvider != null ? _deviceIndexProvider() : OpenVR.k_unTrackedDeviceIndexInvalid;
        if (index == OpenVR.k_unTrackedDeviceIndexInvalid || !system.IsTrackedDeviceConnected(index))
        {
            if (_deviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid) Clear();
            _deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
            ShowFallback(true);
            return;
        }

        if (index != _deviceIndex || Time.unscaledTime >= _nextRetry)
        {
            _nextRetry = Time.unscaledTime + 2f;
            var name = GetStringProperty(system, index, ETrackedDeviceProperty.Prop_RenderModelName_String);
            if (index != _deviceIndex || name != _renderModelName)
            {
                Clear();
                _deviceIndex = index;
                _renderModelName = name;
                if (!string.IsNullOrEmpty(name)) SetupParts(models, name);
            }
        }

        bool allBuilt = _parts.Count > 0;
        foreach (var part in _parts)
        {
            if (!part.Built) BuildPart(part);
            allBuilt &= part.Built;
        }
        if (allBuilt && !_parts.Exists(p => p.Go != null)) allBuilt = false; // every part failed

        UpdateComponentStates(system, models);
        ShowFallback(!allBuilt);
    }

    void SetupParts(CVRRenderModels models, string name)
    {
        uint count = models.GetComponentCount(name);
        for (uint i = 0; i < count; i++)
        {
            var sb = new StringBuilder(128);
            uint len = models.GetComponentName(name, i, sb, (uint)sb.Capacity);
            if (len == 0) continue;
            var componentName = sb.ToString();

            var rm = new StringBuilder(256);
            len = models.GetComponentRenderModelName(name, componentName, rm, (uint)rm.Capacity);
            if (len == 0) continue; // attach points such as "tip" have no geometry

            _parts.Add(new Part { ComponentName = componentName, ModelName = rm.ToString() });
        }

        if (_parts.Count == 0) _parts.Add(new Part { ModelName = name });
        Plugin.Logger.LogInfo($"ControllerModel: device {_deviceIndex} uses '{name}' ({_parts.Count} part(s))");
    }

    void BuildPart(Part part)
    {
        var state = AssetCache.Poll(part.ModelName, out var asset);
        if (state == LoadState.Loading) return;
        if (state == LoadState.Failed)
        {
            part.Built = true; // nothing to build; leave hidden
            part.Go = null;
            return;
        }

        var go = new GameObject(part.ComponentName ?? part.ModelName);
        go.layer = _layer;
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = asset.Material;
        mr.sortingOrder = VRLayers.HandsOrder;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        part.Go = go;
        part.Built = true;
    }

    void UpdateComponentStates(CVRSystem system, CVRRenderModels models)
    {
        if (string.IsNullOrEmpty(_renderModelName)) return;

        ulong devicePath = _devicePathProvider != null ? _devicePathProvider() : 0;
        var controllerState = new VRControllerState_t();
        bool haveLegacyState = false;
        if (devicePath == 0)
            haveLegacyState = system.GetControllerState(_deviceIndex, ref controllerState, (uint)Marshal.SizeOf<VRControllerState_t>());

        foreach (var part in _parts)
        {
            if (part.ComponentName == null || part.Go == null) continue;

            var mode = new RenderModel_ControllerMode_State_t();
            var cs = new RenderModel_ComponentState_t();
            bool ok = devicePath != 0
                ? models.GetComponentStateForDevicePath(_renderModelName, part.ComponentName, devicePath, ref mode, ref cs)
                : haveLegacyState && models.GetComponentState(_renderModelName, part.ComponentName, ref controllerState, ref mode, ref cs);
            if (!ok) continue;

            // RenderModel = the part's pose inside the model (moves when a button is pressed).
            // Local is the attach point for things held in the hand; using it scatters the parts.
            ApplyPose(part.Go.transform, cs.mTrackingToComponentRenderModel);
            bool visible = (cs.uProperties & (uint)EVRComponentProperty.IsVisible) != 0;
            if (part.Go.activeSelf != visible) part.Go.SetActive(visible);
        }
    }

    void Clear()
    {
        foreach (var part in _parts)
            if (part.Go != null) Destroy(part.Go);
        _parts.Clear();
        _renderModelName = null;
    }

    void ShowFallback(bool show)
    {
        if (show && _fallback == null) _fallback = BuildFallback(transform, _layer);
        if (_fallback != null && _fallback.activeSelf != show) _fallback.SetActive(show);
    }

    void OnDestroy() => Clear();

    // ---- helpers ---------------------------------------------------------------------------

    static string GetStringProperty(CVRSystem system, uint index, ETrackedDeviceProperty prop)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        uint size = system.GetStringTrackedDeviceProperty(index, prop, null, 0, ref error);
        if (size <= 1) return null;
        var sb = new StringBuilder((int)size);
        system.GetStringTrackedDeviceProperty(index, prop, sb, size, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : null;
    }

    /// <summary>Applies an OpenVR (right-handed) rigid transform as a Unity local pose.</summary>
    static void ApplyPose(Transform t, HmdMatrix34_t m)
    {
        var pos = new Vector3(m.m3, m.m7, -m.m11);
        var forward = new Vector3(-m.m2, -m.m6, m.m10);
        var up = new Vector3(m.m1, m.m5, -m.m9);
        t.localPosition = pos;
        if (forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f)
            t.localRotation = Quaternion.LookRotation(forward, up);
    }

    static GameObject BuildFallback(Transform parent, int layer)
    {
        GameObject go;
        try
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("ControllerModel: CreatePrimitive failed, using generated box: " + e.Message);
            go = new GameObject();
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
            go.AddComponent<MeshRenderer>();
        }

        go.name = "ShroomVR ControllerFallback";
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, -0.01f, 0.03f);
        go.transform.localScale = new Vector3(0.035f, 0.03f, 0.12f);
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = AssetCache.FallbackMaterial();
        mr.sortingOrder = VRLayers.HandsOrder;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return go;
    }

    static Mesh BoxMesh()
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
            v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
        int[] tris =
        {
            0, 2, 1, 1, 2, 3, // -z
            4, 5, 6, 5, 7, 6, // +z
            0, 1, 4, 1, 5, 4, // -y
            2, 6, 3, 3, 6, 7, // +y
            0, 4, 2, 2, 4, 6, // -x
            1, 3, 5, 3, 7, 5, // +x
        };
        var mesh = new Mesh { name = "ShroomVR Box" };
        mesh.vertices = v;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return mesh;
    }

    /// <summary>Returns the first shader name that exists in this build, logging the choice.</summary>
    public static Shader PickShader(params string[] names)
    {
        foreach (var n in names)
        {
            var s = Shader.Find(n);
            if (s != null)
            {
                Plugin.Logger.LogInfo($"ControllerModel: using shader '{n}'");
                return s;
            }
        }
        Plugin.Logger.LogWarning("ControllerModel: none of the shaders exist: " + string.Join(", ", names));
        return null;
    }

    static readonly string[] ModelShaders =
    {
        "Unlit/Texture", "Sprites/Default", "UI/Unlit/Transparent", "UI/Default", "Standard",
        "Legacy Shaders/Diffuse", "Hidden/Internal-Colored",
    };

    // ---- shared asset cache ----------------------------------------------------------------

    enum LoadState { Loading, Ready, Failed }

    class Asset
    {
        public Mesh Mesh;
        public Material Material;
    }

    /// <summary>Loads render models and textures once and shares them between both hands.</summary>
    static class AssetCache
    {
        class Pending
        {
            public IntPtr Model;
            public int TextureId = -1;
            public bool Failed;
        }

        static readonly Dictionary<string, Asset> Assets = new();
        static readonly Dictionary<string, Pending> PendingModels = new();
        static readonly Dictionary<int, Material> Materials = new();
        static readonly Dictionary<int, IntPtr> PendingTextures = new();
        static Shader _shader;
        static bool _shaderPicked;
        static Material _fallbackMaterial;

        static Shader ModelShader()
        {
            if (!_shaderPicked)
            {
                _shader = PickShader(ModelShaders);
                _shaderPicked = true;
            }
            return _shader;
        }

        static Color TintColor() => new(Tint.r * Brightness, Tint.g * Brightness, Tint.b * Brightness, Tint.a);

        public static Material FallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;
            var shader = ModelShader();
            _fallbackMaterial = shader != null ? new Material(shader) : new Material(Shader.Find("Hidden/Internal-Colored"));
            _fallbackMaterial.name = "ShroomVR ControllerFallback";
            _fallbackMaterial.color = TintColor() * new Color(0.75f, 0.75f, 0.8f, 1f);
            _fallbackMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _fallbackMaterial;
        }

        public static LoadState Poll(string name, out Asset asset)
        {
            if (Assets.TryGetValue(name, out asset))
            {
                // Runtime assets can be destroyed by UnloadUnusedAssets despite the hide flags.
                if (asset == null) return LoadState.Failed;
                if (asset.Mesh != null && asset.Material != null) return LoadState.Ready;
                Assets.Remove(name);
            }

            var models = OpenVR.RenderModels;
            if (models == null) return LoadState.Failed;

            if (!PendingModels.TryGetValue(name, out var pending))
                PendingModels[name] = pending = new Pending();
            if (pending.Failed) return LoadState.Failed;

            if (pending.Model == IntPtr.Zero)
            {
                var ptr = IntPtr.Zero;
                var err = models.LoadRenderModel_Async(name, ref ptr);
                if (err == EVRRenderModelError.Loading) return LoadState.Loading;
                if (err != EVRRenderModelError.None || ptr == IntPtr.Zero)
                {
                    Plugin.Logger.LogWarning($"ControllerModel: render model '{name}' failed: {err}");
                    pending.Failed = true;
                    Assets[name] = null;
                    return LoadState.Failed;
                }
                pending.Model = ptr;
                pending.TextureId = Marshal.PtrToStructure<RenderModel_t>(ptr).diffuseTextureId;
            }

            Material material;
            if (pending.TextureId < 0)
            {
                material = FallbackMaterial();
            }
            else
            {
                var texState = PollMaterial(models, pending.TextureId, out material);
                if (texState == LoadState.Loading) return LoadState.Loading;
                if (texState == LoadState.Failed) material = FallbackMaterial();
            }

            Mesh mesh;
            try
            {
                mesh = BuildMesh(Marshal.PtrToStructure<RenderModel_t>(pending.Model), name);
            }
            finally
            {
                models.FreeRenderModel(pending.Model);
                PendingModels.Remove(name);
            }

            asset = new Asset { Mesh = mesh, Material = material };
            Assets[name] = asset;
            return LoadState.Ready;
        }

        static LoadState PollMaterial(CVRRenderModels models, int textureId, out Material material)
        {
            if (Materials.TryGetValue(textureId, out material) && material != null) return LoadState.Ready;

            if (!PendingTextures.TryGetValue(textureId, out var ptr) || ptr == IntPtr.Zero)
            {
                ptr = IntPtr.Zero;
                var err = models.LoadTexture_Async(textureId, ref ptr);
                if (err == EVRRenderModelError.Loading) return LoadState.Loading;
                if (err != EVRRenderModelError.None || ptr == IntPtr.Zero)
                {
                    Plugin.Logger.LogWarning($"ControllerModel: texture {textureId} failed: {err}");
                    return LoadState.Failed;
                }
                PendingTextures[textureId] = ptr;
            }

            try
            {
                var map = Marshal.PtrToStructure<RenderModel_TextureMap_t>(ptr);
                var tex = BuildTexture(map);
                var shader = ModelShader();
                material = new Material(shader != null ? shader : Shader.Find("Hidden/Internal-Colored"))
                {
                    name = "ShroomVR Controller " + textureId,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                if (tex != null) material.mainTexture = tex;
                material.color = TintColor();
                Materials[textureId] = material;
                return LoadState.Ready;
            }
            finally
            {
                models.FreeTexture(ptr);
                PendingTextures.Remove(textureId);
            }
        }

        static Mesh BuildMesh(RenderModel_t model, string name)
        {
            int vertexCount = (int)model.unVertexCount;
            int triangleCount = (int)model.unTriangleCount;
            int stride = Marshal.SizeOf<RenderModel_Vertex_t>();

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var v = Marshal.PtrToStructure<RenderModel_Vertex_t>(model.rVertexData + i * stride);
                positions[i] = new Vector3(v.vPosition.v0, v.vPosition.v1, -v.vPosition.v2);
                normals[i] = new Vector3(v.vNormal.v0, v.vNormal.v1, -v.vNormal.v2);
                uvs[i] = new Vector2(v.rfTextureCoord0, v.rfTextureCoord1);
            }

            int indexCount = triangleCount * 3;
            var raw = new short[indexCount];
            Marshal.Copy(model.rIndexData, raw, 0, indexCount);
            var triangles = new int[indexCount];
            for (int t = 0; t < triangleCount; t++)
            {
                // Reverse winding for the right-handed to left-handed conversion.
                triangles[t * 3 + 0] = (ushort)raw[t * 3 + 2];
                triangles[t * 3 + 1] = (ushort)raw[t * 3 + 1];
                triangles[t * 3 + 2] = (ushort)raw[t * 3 + 0];
            }

            var mesh = new Mesh { name = "ShroomVR " + name };
            if (vertexCount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        static Texture2D BuildTexture(RenderModel_TextureMap_t map)
        {
            int w = map.unWidth, h = map.unHeight;
            if (w == 0 || h == 0 || map.rubTextureMapData == IntPtr.Zero) return null;

            TextureFormat format;
            int size;
            switch (map.format)
            {
                case EVRRenderModelTextureFormat.RGBA8_SRGB:
                    format = TextureFormat.RGBA32; size = w * h * 4; break;
                case EVRRenderModelTextureFormat.BC4:
                    format = TextureFormat.BC4; size = BlockSize(w, h, 8); break;
                case EVRRenderModelTextureFormat.BC7:
                case EVRRenderModelTextureFormat.BC7_SRGB:
                    format = TextureFormat.BC7; size = BlockSize(w, h, 16); break;
                case EVRRenderModelTextureFormat.RGBA16_FLOAT:
                    format = TextureFormat.RGBAHalf; size = w * h * 8; break;
                default:
                    Plugin.Logger.LogWarning($"ControllerModel: unsupported texture format {map.format}");
                    return null;
            }

            if (!SystemInfo.SupportsTextureFormat(format))
            {
                Plugin.Logger.LogWarning($"ControllerModel: GPU does not support {format}");
                return null;
            }

            var tex = new Texture2D(w, h, format, false, false)
            {
                name = "ShroomVR ControllerTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            tex.LoadRawTextureData(map.rubTextureMapData, size);
            tex.Apply(false, true);
            return tex;
        }

        static int BlockSize(int w, int h, int bytesPerBlock) => ((w + 3) / 4) * ((h + 3) / 4) * bytesPerBlock;
    }
}
