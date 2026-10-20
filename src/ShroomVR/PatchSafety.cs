using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;

namespace ShroomVR;

/// <summary>
/// GameAssembly.dll is linked with identical-code folding: byte-identical methods share one native
/// body. Harmony detours that body, so patching e.g. a one-line getter silently patches every other
/// getter that compiled to the same instructions. (Patching BasicInputSource.get_DeviceType also
/// rewrote AbstractCardData.get_Icon and wiped the art off every card.) After patching, every target
/// whose native code is shared with any other method is unpatched and reported.
/// </summary>
public static class PatchSafety
{
    public static unsafe void Verify(Harmony harmony)
    {
        var targets = new Dictionary<IntPtr, MethodBase>();
        foreach (var m in harmony.GetPatchedMethods())
        {
            var code = NativeCode(m);
            if (code != IntPtr.Zero) targets[code] = m;
        }
        if (targets.Count == 0) return;

        var sharers = new Dictionary<IntPtr, List<string>>();
        uint count = 0;
        var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (uint a = 0; a < count; a++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
            uint classes = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < classes; c++)
            {
                var klass = IL2CPP.il2cpp_image_get_class(image, c);
                IntPtr iter = IntPtr.Zero, method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    var code = *(IntPtr*)method; // Il2CppMethodInfo.methodPointer
                    if (!targets.ContainsKey(code)) continue;
                    if (!sharers.TryGetValue(code, out var list)) sharers[code] = list = new List<string>();
                    list.Add(IL2CPP.il2cpp_class_get_name_(klass) + "." + IL2CPP.il2cpp_method_get_name_(method));
                }
            }
        }

        foreach (var (code, target) in targets)
        {
            if (!sharers.TryGetValue(code, out var list) || list.Count <= 1) continue;
            harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id);
            Plugin.Logger.LogError($"Not patching {target.DeclaringType?.Name}.{target.Name}: its native code is shared with " +
                                   string.Join(", ", list.Distinct().Take(8)) + (list.Count > 8 ? ", ..." : ""));
        }
    }

    static unsafe IntPtr NativeCode(MethodBase m)
    {
        try
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
            var info = field != null ? (IntPtr)field.GetValue(null) : IntPtr.Zero;
            return info == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)info;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
