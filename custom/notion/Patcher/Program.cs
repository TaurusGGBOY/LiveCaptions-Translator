using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("merged.dll patched.dll original.dll");
using var module = ModuleDefinition.ReadModule(args[0]);
using var original = ModuleDefinition.ReadModule(args[2]);
// WPF pack URIs refer to this assembly identity, regardless of output filename.
module.Assembly.Name.Name = original.Assembly.Name.Name;
module.Name = original.Name;
IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(All(t.NestedTypes)));
var hooks = module.Types.Single(t => t.FullName == "LiveCaptionsNotion.Hooks");
var changed = new HashSet<string>();
void BeforeReturns(string typeName, string methodName, string hookName)
{
    var method = module.Types.Single(t => t.FullName == typeName).Methods.Single(m => m.Name == methodName);
    var hook = hooks.Methods.Single(m => m.Name == hookName);
    var il = method.Body.GetILProcessor();
    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    { il.InsertBefore(ret, il.Create(OpCodes.Ldarg_0)); il.InsertBefore(ret, il.Create(OpCodes.Call, hook)); }
    changed.Add(method.FullName);
}
BeforeReturns("LiveCaptionsTranslator.CaptionPage", ".ctor", "Attach");
BeforeReturns("LiveCaptionsTranslator.models.TranslationTask", ".ctor", "Register");
BeforeReturns("LiveCaptionsTranslator.HistoryPage", ".ctor", "AttachHistory");
var historySetter = module.Types.Single(t => t.FullName == "LiveCaptionsNotion.HistoryView").Methods.Single(m => m.Name == "SetItems");
var historyRefresh = All(module.Types).Where(t => t.FullName.StartsWith("LiveCaptionsTranslator.HistoryPage"))
    .SelectMany(t => t.Methods).Single(m => m.HasBody && m.Name.Contains("<LoadHistory>") && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "set_ItemsSource"));
var assign = historyRefresh.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "set_ItemsSource");
assign.OpCode = OpCodes.Call; assign.Operand = historySetter; changed.Add(historyRefresh.FullName);
var state = All(module.Types).Single(t => t.FullName.Contains("TranslationTaskQueue/<OnTaskCompleted>d__"));
var move = state.Methods.Single(m => m.Name == "MoveNext");
var task = state.Fields.Single(f => f.Name == "translationTask");
var store = move.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "output");
var processor = move.Body.GetILProcessor();
var one = processor.Create(OpCodes.Ldarg_0); var two = processor.Create(OpCodes.Ldfld, task);
processor.InsertAfter(store, one); processor.InsertAfter(one, two);
processor.InsertAfter(two, processor.Create(OpCodes.Call, hooks.Methods.Single(m => m.Name == "Publish")));
changed.Add(move.FullName);

// ILRepack can rewrite the BAML resource container. Restore original bytes so
// unrelated personalized windows, theme assets and overlay behavior stay intact.
foreach (var resource in original.Resources.OfType<EmbeddedResource>())
{
    var merged = module.Resources.Single(r => r.Name == resource.Name); module.Resources.Remove(merged);
    module.Resources.Add(new EmbeddedResource(resource.Name, resource.Attributes, resource.GetResourceData()));
}
module.Write(args[1]);
using var after = ModuleDefinition.ReadModule(args[1]);
int verified = 0;
foreach (var type in All(original.Types).Where(t => t.Name != "<Module>"))
{
    var match = All(after.Types).Single(t => t.FullName == type.FullName);
    foreach (var method in type.Methods.Where(m => m.HasBody && !changed.Contains(m.FullName)))
    {
        var other = match.Methods.Single(m => m.FullName == method.FullName);
        string Body(MethodDefinition m) => string.Join("\n", m.Body.Instructions.Select(i => i.ToString()));
        if (Body(method) != Body(other)) throw new Exception("Unexpected method change: " + method.FullName);
        verified++;
    }
}
foreach (var resource in original.Resources.OfType<EmbeddedResource>())
    if (!resource.GetResourceData().SequenceEqual(after.Resources.OfType<EmbeddedResource>().Single(r => r.Name == resource.Name).GetResourceData()))
        throw new Exception("Resource changed: " + resource.Name);
Console.WriteLine($"Patched exactly {changed.Count} integration methods; verified {verified} other method bodies and original resources unchanged.");
