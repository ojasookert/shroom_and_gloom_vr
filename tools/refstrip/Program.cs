// Turns the interop assemblies BepInEx generates from the game into reference assemblies:
// all metadata is kept, every method body becomes `throw null` and resources are dropped.
// Usage: refstrip <input dir> <output dir> [assembly names...]
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: refstrip <input dir> <output dir> [assembly names...]");
    return 1;
}

var input = args[0];
var output = args[1];
var only = args.Skip(2).Select(n => n.EndsWith(".dll") ? n : n + ".dll").ToHashSet(StringComparer.OrdinalIgnoreCase);
Directory.CreateDirectory(output);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(input);

foreach (var path in Directory.GetFiles(input, "*.dll").OrderBy(p => p))
{
    var name = Path.GetFileName(path);
    if (only.Count > 0 && !only.Contains(name)) continue;
    using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
    foreach (var module in assembly.Modules)
    {
        foreach (var type in AllTypes(module.Types))
        {
            foreach (var method in type.Methods)
            {
                DropInteropAttributes(method.CustomAttributes);
                if (!method.HasBody) continue;
                var body = new MethodBody(method);
                var il = body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Throw);
                method.Body = body;
            }
            // Il2CppInterop's private native pointer fields aren't part of the API; instance fields stay
            // so structs keep their layout.
            foreach (var field in type.Fields.Where(f => f.IsStatic && !f.IsPublic && !f.IsFamily).ToList())
                type.Fields.Remove(field);
            foreach (var field in type.Fields) DropInteropAttributes(field.CustomAttributes);
            DropInteropAttributes(type.CustomAttributes);
        }
        module.Resources.Clear();
    }
    assembly.Write(Path.Combine(output, name));
    Console.WriteLine(name);
}
return 0;

// Caller counts, xref scan results and obfuscated names only matter to the interop runtime.
static void DropInteropAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
{
    for (int i = attributes.Count - 1; i >= 0; i--)
        if (attributes[i].AttributeType.Namespace.StartsWith("Il2CppInterop")) attributes.RemoveAt(i);
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) =>
    types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
