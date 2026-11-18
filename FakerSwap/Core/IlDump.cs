using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace FakerSwap.Core
{
    /// <summary>
    /// Runtime IL dumper.
    ///
    /// The shipped `Assembly-CSharp.dll` has its method bodies stripped/encrypted on disk (every
    /// method decompiles to "Invalid MethodBodyBlock: Invalid method header: 0x00"), yet the game
    /// obviously executes them.  The IL therefore only exists in memory, so a static decompiler is
    /// useless for the parts we need to understand (MODE SELECT's `UpdateInput`, `GameProcess.OnStart`
    /// ...).  This dumper asks Mono for the *loaded* method body and disassembles it, resolving every
    /// token to a real name, so we can read the exact state machine offline.
    ///
    /// Usage (write to `Package/FakerSwap/command.txt`):
    ///   ildump ModeSelectProcess              -> every method of that type (substring match)
    ///   ildump ModeSelectProcess UpdateInput  -> just that method
    ///   ildump GameProcess OnStart
    ///   ildump Manager.InputManager GetButtonPush
    ///
    /// The result is written to `Package/FakerSwap/Logs/ildump-*.txt` (never truncated).
    /// </summary>
    public static class IlDump
    {
        private static Dictionary<ushort, OpCode> _ops;
        private static readonly object _gate = new object();

        public static void Dump(string typeFilter, string methodFilter)
        {
            lock (_gate)
            {
                try
                {
                    DoDump(typeFilter ?? "", methodFilter ?? "");
                }
                catch (Exception e)
                {
                    Log.Error("[ildump] 失败", e);
                }
            }
        }

        /// <summary>
        /// Find every method that CALLS into the given type (or a specific member of it).
        /// e.g. `xref Process.RegionalSelectProcess .ctor` -> who creates the area-select screen.
        /// Scans the loaded IL of every method in Assembly-CSharp; results go to a log file.
        /// </summary>
        public static void Xref(string typeFilter, string memberFilter)
        {
            lock (_gate)
            {
                try
                {
                    DoXref(typeFilter ?? "", memberFilter ?? "");
                }
                catch (Exception e)
                {
                    Log.Error("[xref] 失败", e);
                }
            }
        }

        private static void DoXref(string typeFilter, string memberFilter)
        {
            if (string.IsNullOrEmpty(typeFilter))
            {
                Log.Warn("[xref] 用法: xref <类型名片段> [成员名片段，默认 .ctor]");
                return;
            }
            if (string.IsNullOrEmpty(memberFilter)) memberFilter = ".ctor";

            var sb = new StringBuilder(32 * 1024);
            sb.AppendLine("# FakerSwap IL xref: callers of " + typeFilter + " :: " + memberFilter);
            sb.AppendLine("# when   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            var ops = OpTable();
            var types = LoadedTypes();
            int scanned = 0, hits = 0;
            for (int ti = 0; ti < types.Length; ti++)
            {
                var t = types[ti];
                if (t == null) continue;
                MethodBase[] methods;
                try { methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception) { continue; }
                for (int mi = 0; mi < methods.Length; mi++)
                {
                    var method = methods[mi];
                    if (method == null || method.IsAbstract) continue;
                    byte[] il;
                    Module module;
                    try
                    {
                        var body = method.GetMethodBody();
                        if (body == null) continue;
                        il = body.GetILAsByteArray();
                        module = method.Module;
                    }
                    catch (Exception) { continue; }
                    if (il == null || il.Length == 0) continue;
                    scanned++;

                    int pos = 0, guard = 0;
                    bool reported = false;
                    while (pos < il.Length && guard++ < 200000 && !reported)
                    {
                        ushort key;
                        if (il[pos] == 0xFE)
                        {
                            if (pos + 1 >= il.Length) break;
                            key = (ushort)(0xFE00 | il[pos + 1]);
                            pos += 2;
                        }
                        else { key = il[pos]; pos += 1; }
                        OpCode op;
                        if (!ops.TryGetValue(key, out op)) break;

                        int operandSize = OperandSize(op);
                        if ((op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineTok) &&
                            pos + 4 <= il.Length)
                        {
                            int token = BitConverter.ToInt32(il, pos);
                            try
                            {
                                var callee = module.ResolveMember(token, null, null);
                                if (callee != null && MatchesCallee(callee, typeFilter, memberFilter))
                                {
                                    hits++;
                                    sb.Append("CALLER ").Append(SafeName(t)).Append("::").Append(method.Name)
                                      .Append("  ->  ").Append(SafeName(callee.DeclaringType)).Append("::").Append(callee.Name)
                                      .AppendLine();
                                    reported = true;
                                }
                            }
                            catch (Exception) { }
                        }
                        // Skip operands safely.
                        try { pos = SkipOperand(il, pos, op); }
                        catch (Exception) { break; }
                    }
                }
            }
            sb.AppendLine();
            sb.AppendLine("# scanned methods=" + scanned + " hits=" + hits);
            string path = System.IO.Path.Combine(Fs.LogDir, "xref-" + DateTime.Now.ToString("HHmmss") + ".txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
            catch (Exception e) { Log.Error("[xref] 写文件失败", e); }
            Log.Info("[xref] 扫描 " + scanned + " 个方法，命中 " + hits + " 处 → " + path);
        }

        private static bool MatchesCallee(MemberInfo callee, string typeFilter, string memberFilter)
        {
            try
            {
                var declaring = callee.DeclaringType;
                if (declaring == null) return false;
                string dt = SafeName(declaring);
                if (!Matches(dt, typeFilter)) return false;
                if (string.IsNullOrEmpty(memberFilter)) return true;
                string name = callee.Name;
                if (name == memberFilter) return true;
                // ".ctor" also matches the constructor display name.
                if (memberFilter == ".ctor" && name == ".ctor") return true;
                return Matches(name, memberFilter);
            }
            catch (Exception) { return false; }
        }

        private static int SkipOperand(byte[] il, int pos, OpCode op)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone: return pos;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget: return pos + 1;
                case OperandType.InlineVar: return pos + 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return pos + 8;
                case OperandType.InlineSwitch:
                {
                    if (pos + 4 > il.Length) return il.Length;
                    int n = BitConverter.ToInt32(il, pos);
                    return Math.Min(il.Length, pos + 4 + n * 4);
                }
                default:
                    return Math.Min(il.Length, pos + 4);
            }
        }


        private static void DoDump(string typeFilter, string methodFilter)
        {
            if (string.IsNullOrEmpty(typeFilter))
            {
                Log.Warn("[ildump] 用法: ildump <类型名片段> [方法名片段]");
                return;
            }

            var sb = new StringBuilder(64 * 1024);
            sb.AppendLine("# FakerSwap runtime IL dump");
            sb.AppendLine("# when   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# filter : type~" + typeFilter + " method~" + (string.IsNullOrEmpty(methodFilter) ? "*" : methodFilter));
            sb.AppendLine();

            int dumpedTypes = 0, dumpedMethods = 0, failed = 0;
            Type[] types = LoadedTypes();
            sb.AppendLine("# Assembly-CSharp types loaded: " + types.Length);
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (t == null) continue;
                string full = SafeName(t);
                if (!Matches(full, typeFilter)) continue;

                dumpedTypes++;
                sb.AppendLine("// ===========================================================================");
                sb.AppendLine("// TYPE " + full + (t.BaseType != null ? " : " + SafeName(t.BaseType) : ""));
                sb.AppendLine("// ===========================================================================");
                DumpShape(sb, t);

                MethodBase[] methods;
                try { methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception e) { methods = new MethodBase[0]; sb.AppendLine("// GetMethods failed: " + e.Message); }

                // Constructors too - the MODE SELECT crash happened in OnStart, but ctors are cheap.
                var all = new List<MethodBase>();
                all.AddRange(methods);
                try { all.AddRange(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)); }
                catch (Exception) { }

                for (int m = 0; m < all.Count; m++)
                {
                    var mi = all[m];
                    if (mi == null) continue;
                    if (!string.IsNullOrEmpty(methodFilter) && !Matches(mi.Name, methodFilter)) continue;
                    if (mi.IsAbstract) continue;
                    sb.AppendLine();
                    try
                    {
                        DumpMethod(sb, mi);
                        dumpedMethods++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        sb.AppendLine("// ---- " + mi.Name + " ----");
                        sb.AppendLine("// !! " + e.GetType().Name + ": " + e.Message);
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("# dumped types=" + dumpedTypes + " methods=" + dumpedMethods + " failed=" + failed);
            string path = System.IO.Path.Combine(Fs.LogDir, "ildump-" + DateTime.Now.ToString("HHmmss") + ".txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
            catch (Exception e) { Log.Error("[ildump] 写文件失败: " + path, e); }

            if (dumpedTypes == 0)
                Log.Warn("[ildump] 没有任何类型匹配 '" + typeFilter + "'（结果仍写入 " + path + "）");
            else
                Log.Info("[ildump] 已导出 " + dumpedTypes + " 个类型 / " + dumpedMethods + " 个方法（失败 " + failed +
                         "）→ " + path);

            // A short preview in the mod log so the operator sees something without opening the file.
            Log.Info("[ildump] 预览: " + typeFilter + " 的方法 = " + PreviewMethods(types, typeFilter, methodFilter));
        }

        private static string PreviewMethods(Type[] types, string typeFilter, string methodFilter)
        {
            var sb = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < types.Length && shown < 40; i++)
            {
                var t = types[i];
                if (t == null || !Matches(SafeName(t), typeFilter)) continue;
                try
                {
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (!string.IsNullOrEmpty(methodFilter) && !Matches(m.Name, methodFilter)) continue;
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(m.Name);
                        if (++shown >= 40) break;
                    }
                }
                catch (Exception) { }
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static Type[] LoadedTypes()
        {
            var asm = typeof(Manager.InputManager).Assembly;
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                var list = new List<Type>();
                foreach (var t in e.Types) if (t != null) list.Add(t);
                return list.ToArray();
            }
        }

        private static bool Matches(string name, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeName(Type t)
        {
            try { return t.FullName ?? t.Name; } catch (Exception) { return "?"; }
        }

        private static string NameOf(MemberInfo m)
        {
            if (m == null) return "<null>";
            var t = m as Type;
            if (t != null) return SafeName(t);
            string owner = "";
            try { owner = m.DeclaringType != null ? m.DeclaringType.Name + "." : ""; } catch (Exception) { }
            return owner + m.Name;
        }

        // ------------------------------------------------------------------ type shape

        private static void DumpShape(StringBuilder sb, Type t)
        {
            try
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    sb.Append("// FIELD ").Append(f.IsStatic ? "static " : "").Append(TypeName(f.FieldType)).Append(' ')
                      .Append(f.Name).AppendLine(";");
                }
            }
            catch (Exception e) { sb.AppendLine("// fields failed: " + e.Message); }
        }

        private static string TypeName(Type t)
        {
            if (t == null) return "?";
            try
            {
                if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
                if (t.IsByRef) return TypeName(t.GetElementType()) + "&";
                string n = t.Name;
                return string.IsNullOrEmpty(n) ? t.ToString() : n;
            }
            catch (Exception) { return "?"; }
        }

        // ------------------------------------------------------------------ method body

        private static void DumpMethod(StringBuilder sb, MethodBase mi)
        {
            sb.Append("// ---- ").Append(mi.IsStatic ? "static " : "").Append(TypeName((mi as MethodInfo) != null ? ((MethodInfo)mi).ReturnType : typeof(void)))
              .Append(' ').Append(mi.Name).Append('(');
            var ps = mi.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeName(ps[i].ParameterType)).Append(' ').Append(ps[i].Name);
            }
            sb.AppendLine(") ----");
            try
            {
                sb.AppendLine("// RVA = 0x" + mi.MethodHandle.GetFunctionPointer().ToInt64().ToString("X"));
            }
            catch (Exception) { }

            MethodBody body;
            try { body = mi.GetMethodBody(); }
            catch (Exception e)
            {
                sb.AppendLine("// !! GetMethodBody: " + e.GetType().Name + ": " + e.Message);
                return;
            }
            if (body == null) { sb.AppendLine("// (no body / abstract / extern)"); return; }

            byte[] il;
            try { il = body.GetILAsByteArray(); }
            catch (Exception e) { sb.AppendLine("// !! GetILAsByteArray: " + e.Message); return; }
            if (il == null || il.Length == 0) { sb.AppendLine("// (empty IL)"); return; }

            sb.Append("// IL bytes = ").Append(il.Length).Append("  head = ");
            for (int i = 0; i < il.Length && i < 24; i++) sb.Append(il[i].ToString("X2")).Append(' ');
            sb.AppendLine();

            try
            {
                if (body.LocalVariables != null && body.LocalVariables.Count > 0)
                {
                    sb.Append("// locals: ");
                    for (int i = 0; i < body.LocalVariables.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append('V').Append(i).Append(':').Append(TypeName(body.LocalVariables[i].LocalType));
                    }
                    sb.AppendLine();
                }
            }
            catch (Exception) { }

            try
            {
                if (body.ExceptionHandlingClauses != null && body.ExceptionHandlingClauses.Count > 0)
                {
                    foreach (var c in body.ExceptionHandlingClauses)
                    {
                        sb.Append("// EH ").Append(c.Flags)
                          .Append(" try IL_").Append(c.TryOffset.ToString("X4")).Append("+").Append(c.TryLength)
                          .Append(" handler IL_").Append(c.HandlerOffset.ToString("X4")).Append("+").Append(c.HandlerLength);
                        if (c.Flags == ExceptionHandlingClauseOptions.Clause)
                            sb.Append(" catch ").Append(TypeName(c.CatchType));
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception) { }

            var module = mi.Module;
            var ops = OpTable();
            int pos = 0;
            int guard = 0;
            while (pos < il.Length && guard++ < 100000)
            {
                int start = pos;
                ushort key;
                if (il[pos] == 0xFE)
                {
                    if (pos + 1 >= il.Length) break;
                    key = (ushort)(0xFE00 | il[pos + 1]);
                    pos += 2;
                }
                else
                {
                    key = il[pos];
                    pos += 1;
                }

                OpCode op;
                if (!ops.TryGetValue(key, out op))
                {
                    sb.Append("IL_").Append(start.ToString("X4")).Append(": <unknown 0x").Append(key.ToString("X4")).AppendLine(">");
                    break;
                }

                sb.Append("IL_").Append(start.ToString("X4")).Append(": ").Append(op.Name.PadRight(12));
                try
                {
                    pos = AppendOperand(sb, il, pos, op, module, start);
                }
                catch (Exception e)
                {
                    sb.Append(" <operand error: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('>');
                    pos = Math.Min(il.Length, pos + OperandSize(op));
                }
                sb.AppendLine();
            }
        }

        private static int OperandSize(OpCode op)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4;
                default: return 4;
            }
        }

        private static int AppendOperand(StringBuilder sb, byte[] il, int pos, OpCode op, Module module, int instrStart)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    return pos;

                case OperandType.ShortInlineI:
                {
                    sbyte v = (sbyte)il[pos];
                    sb.Append(v);
                    return pos + 1;
                }
                case OperandType.InlineI:
                {
                    int v = BitConverter.ToInt32(il, pos);
                    sb.Append(v);
                    return pos + 4;
                }
                case OperandType.InlineI8:
                {
                    long v = BitConverter.ToInt64(il, pos);
                    sb.Append(v);
                    return pos + 8;
                }
                case OperandType.ShortInlineR:
                {
                    float v = BitConverter.ToSingle(il, pos);
                    sb.Append(v.ToString("R"));
                    return pos + 4;
                }
                case OperandType.InlineR:
                {
                    double v = BitConverter.ToDouble(il, pos);
                    sb.Append(v.ToString("R"));
                    return pos + 8;
                }
                case OperandType.ShortInlineVar:
                {
                    sb.Append("V").Append(il[pos]);
                    return pos + 1;
                }
                case OperandType.InlineVar:
                {
                    sb.Append("V").Append(BitConverter.ToUInt16(il, pos));
                    return pos + 2;
                }
                case OperandType.ShortInlineBrTarget:
                {
                    sbyte d = (sbyte)il[pos];
                    int target = pos + 1 + d;
                    sb.Append("IL_").Append(target.ToString("X4"));
                    return pos + 1;
                }
                case OperandType.InlineBrTarget:
                {
                    int d = BitConverter.ToInt32(il, pos);
                    int target = pos + 4 + d;
                    sb.Append("IL_").Append(target.ToString("X4"));
                    return pos + 4;
                }
                case OperandType.InlineSwitch:
                {
                    int n = BitConverter.ToInt32(il, pos);
                    int p = pos + 4;
                    sb.Append("[").Append(n).Append("] ");
                    int baseTarget = p + n * 4;
                    for (int i = 0; i < n; i++)
                    {
                        int d = BitConverter.ToInt32(il, p + i * 4);
                        if (i > 0) sb.Append(", ");
                        sb.Append("IL_").Append((baseTarget + d).ToString("X4"));
                    }
                    return p + n * 4;
                }
                case OperandType.InlineString:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    string s = null;
                    try { s = module.ResolveString(token); } catch (Exception e) { s = "<str? " + e.Message + ">"; }
                    sb.Append('"').Append(s).Append('"');
                    return pos + 4;
                }
                case OperandType.InlineMethod:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append(Resolve(module, token, 1));
                    return pos + 4;
                }
                case OperandType.InlineField:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append(Resolve(module, token, 2));
                    return pos + 4;
                }
                case OperandType.InlineType:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append(Resolve(module, token, 3));
                    return pos + 4;
                }
                case OperandType.InlineTok:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append(Resolve(module, token, 0));
                    return pos + 4;
                }
                case OperandType.InlineSig:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append("sig 0x").Append(token.ToString("X8"));
                    return pos + 4;
                }
                default:
                {
                    int token = BitConverter.ToInt32(il, pos);
                    sb.Append("0x").Append(token.ToString("X8"));
                    return pos + 4;
                }
            }
        }

        private static string Resolve(Module module, int token, int kind)
        {
            try
            {
                switch (kind)
                {
                    case 1:
                    {
                        var m = module.ResolveMethod(token, null, null);
                        return NameOf(m);
                    }
                    case 2:
                    {
                        var f = module.ResolveField(token, null, null);
                        return NameOf(f.DeclaringType) + "." + f.Name;
                    }
                    case 3:
                    {
                        var t = module.ResolveType(token, null, null);
                        return SafeName(t);
                    }
                    default:
                    {
                        var m = module.ResolveMember(token, null, null);
                        return NameOf(m);
                    }
                }
            }
            catch (Exception)
            {
                // Fall back: try every member kind.
                try { return NameOf(module.ResolveMember(token, null, null)); }
                catch (Exception) { return "token 0x" + token.ToString("X8"); }
            }
        }

        private static Dictionary<ushort, OpCode> OpTable()
        {
            if (_ops != null) return _ops;
            var table = new Dictionary<ushort, OpCode>();
            var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                var fi = fields[i];
                if (fi.FieldType != typeof(OpCode)) continue;
                try
                {
                    var op = (OpCode)fi.GetValue(null);
                    table[(ushort)op.Value] = op;
                }
                catch (Exception) { }
            }
            _ops = table;
            return table;
        }
    }
}
