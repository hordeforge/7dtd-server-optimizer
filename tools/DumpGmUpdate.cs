// Dump GameManager.gmUpdate / Update structure for RE notes.
// Usage: DumpGmUpdate.exe <Assembly-CSharp.dll> <outDir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class DumpGmUpdate
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: DumpGmUpdate.exe <Assembly-CSharp.dll> <outDir>");
            return 2;
        }
        string asmPath = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(asmPath));
        var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });

        var gm = asm.MainModule.Types.FirstOrDefault(t => t.Name == "GameManager");
        if (gm == null)
        {
            Console.Error.WriteLine("GameManager type not found");
            return 1;
        }

        var targets = new[]
        {
            "Update", "gmUpdate", "UpdateTick", "FixedUpdate", "LateUpdate",
            "StartGame", "Awake", "OnApplicationQuit"
        };

        var sbIndex = new StringBuilder();
        sbIndex.AppendLine("# GameManager update-path dump");
        sbIndex.AppendLine();
        sbIndex.AppendLine($"Assembly: `{asmPath}`");
        sbIndex.AppendLine($"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sbIndex.AppendLine();

        foreach (var name in targets)
        {
            foreach (var m in gm.Methods.Where(x => x.Name == name && x.HasBody))
            {
                string baseName = $"GameManager_{m.Name}";
                if (m.Parameters.Count > 0)
                    baseName += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name));
                DumpMethod(m, outDir, baseName, sbIndex);
            }
        }

        // Also dump World.OnUpdateTick / TickEntities for conductor context
        var world = asm.MainModule.Types.FirstOrDefault(t => t.Name == "World");
        if (world != null)
        {
            foreach (var name in new[] { "OnUpdateTick", "TickEntities", "TickEntity", "EntityActivityUpdate", "TickEntitiesSlice" })
            {
                foreach (var m in world.Methods.Where(x => x.Name == name && x.HasBody))
                {
                    string baseName = $"World_{m.Name}";
                    if (m.Parameters.Count > 0)
                        baseName += "_" + string.Join("_", m.Parameters.Select(p => p.ParameterType.Name));
                    DumpMethod(m, outDir, baseName, sbIndex);
                }
            }
        }

        // ConnectionManager.Update
        var cm = asm.MainModule.Types.FirstOrDefault(t => t.Name == "ConnectionManager");
        if (cm != null)
        {
            foreach (var m in cm.Methods.Where(x => x.Name == "Update" && x.HasBody && x.Parameters.Count == 0))
                DumpMethod(m, outDir, "ConnectionManager_Update", sbIndex);
        }

        File.WriteAllText(Path.Combine(outDir, "INDEX.md"), sbIndex.ToString());
        Console.WriteLine("Wrote " + outDir);
        return 0;
    }

    static void DumpMethod(MethodDefinition m, string outDir, string baseName, StringBuilder index)
    {
        var body = m.Body;
        int ilCount = body.Instructions.Count;

        index.AppendLine($"## {m.DeclaringType.FullName}::{m.Name}");
        index.AppendLine();
        index.AppendLine($"- IL instructions: **{ilCount}**");
        index.AppendLine($"- Max stack: {body.MaxStackSize}");
        index.AppendLine($"- Locals: {body.Variables.Count}");
        index.AppendLine($"- Exception handlers: {body.ExceptionHandlers.Count}");
        index.AppendLine($"- Files: `{baseName}_calls.md`, `{baseName}_il.txt`, `{baseName}_flow.md`");
        index.AppendLine();

        // Ordered unique calls with first/last offset and count
        var callEvents = new List<(int offset, string kind, string target)>();
        var dedicatedHits = new List<(int offset, string detail)>();
        var fieldHits = new List<(int offset, string op, string field)>();

        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt)
            {
                string t = FormatMethodRef(ins.Operand);
                callEvents.Add((ins.Offset, ins.OpCode.Code.ToString(), t));
                if (t.IndexOf("IsDedicated", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("isDedicated", StringComparison.Ordinal) >= 0
                    || t.IndexOf("get_IsDedicatedServer", StringComparison.Ordinal) >= 0)
                    dedicatedHits.Add((ins.Offset, t));
            }
            else if (ins.OpCode.Code == Code.Newobj && ins.Operand is MethodReference mr)
            {
                callEvents.Add((ins.Offset, "Newobj", mr.DeclaringType.FullName + "::.ctor"));
            }
            else if (ins.OpCode.Code == Code.Ldfld || ins.OpCode.Code == Code.Ldsfld
                  || ins.OpCode.Code == Code.Stfld || ins.OpCode.Code == Code.Stsfld)
            {
                if (ins.Operand is FieldReference fr)
                    fieldHits.Add((ins.Offset, ins.OpCode.Code.ToString(), fr.DeclaringType.Name + "::" + fr.Name));
            }
            else if (ins.OpCode.Code == Code.Brfalse || ins.OpCode.Code == Code.Brfalse_S
                  || ins.OpCode.Code == Code.Brtrue || ins.OpCode.Code == Code.Brtrue_S)
            {
                // annotated in flow via previous call if dedicated
            }
        }

        // calls.md: ordered sequence + frequency
        var callsMd = new StringBuilder();
        callsMd.AppendLine($"# Calls: {m.DeclaringType.FullName}::{m.Name}");
        callsMd.AppendLine();
        callsMd.AppendLine($"IL count: {ilCount}");
        callsMd.AppendLine();
        callsMd.AppendLine("## Frequency (top)");
        callsMd.AppendLine();
        callsMd.AppendLine("| Count | Target |");
        callsMd.AppendLine("|---:|---|");
        foreach (var g in callEvents.GroupBy(c => c.target).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            callsMd.AppendLine($"| {g.Count()} | `{Escape(g.Key)}` |");
        callsMd.AppendLine();
        callsMd.AppendLine("## Ordered call sequence (IL offset)");
        callsMd.AppendLine();
        callsMd.AppendLine("| # | IL | Op | Target |");
        callsMd.AppendLine("|---:|---:|---|---|");
        int n = 0;
        foreach (var c in callEvents)
        {
            n++;
            callsMd.AppendLine($"| {n} | IL_{c.offset:X4} | {c.kind} | `{Escape(c.target)}` |");
        }
        callsMd.AppendLine();
        callsMd.AppendLine("## IsDedicated / dedicated-related calls");
        callsMd.AppendLine();
        if (dedicatedHits.Count == 0)
            callsMd.AppendLine("(none in this method)");
        else
            foreach (var d in dedicatedHits)
                callsMd.AppendLine($"- IL_{d.offset:X4}: `{Escape(d.detail)}`");
        callsMd.AppendLine();
        callsMd.AppendLine("## Field access frequency (top 40)");
        callsMd.AppendLine();
        callsMd.AppendLine("| Count | Op+Field |");
        callsMd.AppendLine("|---:|---|");
        foreach (var g in fieldHits.GroupBy(f => f.op + " " + f.field).OrderByDescending(g => g.Count()).Take(40))
            callsMd.AppendLine($"| {g.Count()} | `{Escape(g.Key)}` |");

        File.WriteAllText(Path.Combine(outDir, baseName + "_calls.md"), callsMd.ToString());

        // Full IL
        var il = new StringBuilder();
        il.AppendLine($"// {m.DeclaringType.FullName}::{m.Name}");
        il.AppendLine($"// IL instructions: {ilCount}");
        foreach (var v in body.Variables)
            il.AppendLine($"// local V_{v.Index}: {v.VariableType.FullName}");
        il.AppendLine();
        foreach (var ins in body.Instructions)
        {
            string op = ins.Operand != null ? " " + FormatOperand(ins) : "";
            il.AppendLine($"IL_{ins.Offset:X4}: {ins.OpCode.Name}{op}");
        }
        File.WriteAllText(Path.Combine(outDir, baseName + "_il.txt"), il.ToString());

        // Flow sketch: linear regions split by dedicated checks and major calls
        var flow = new StringBuilder();
        flow.AppendLine($"# Control-flow sketch: {m.DeclaringType.FullName}::{m.Name}");
        flow.AppendLine();
        flow.AppendLine("Linear pass with branch targets and major calls. Not a full CFG decompiler.");
        flow.AppendLine();
        flow.AppendLine("```");
        foreach (var ins in body.Instructions)
        {
            return "\"" + s + "\"";
        return ins.Operand != null ? ins.Operand.ToString() : "";
    }

    static string Escape(string s) => s.Replace("|", "\\|");
}
