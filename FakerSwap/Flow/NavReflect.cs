using System;
using System.Collections.Generic;
using System.Reflection;
using FakerSwap.Core;

namespace FakerSwap.Flow
{
    /// <summary>
    /// 反射小工具。导航层需要碰大量游戏私有成员 / 版本间改名成员，统一走这里：
    ///  - 结果缓存，避免每帧重复查找；
    ///  - 失败只报警一次，避免 Tick 里刷屏；
    ///  - <see cref="Probe"/> 在 Install 时把关键成员是否可用打进日志，实机一眼定位版本不符。
    /// </summary>
    internal static class NavReflect
    {
        private const BindingFlags All =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy;

        private static readonly Dictionary<string, FieldInfo> FieldCache = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> MethodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> PropCache = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, ConstructorInfo> CtorCache = new Dictionary<string, ConstructorInfo>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        // ------------------------------------------------------------------ lookup

        public static FieldInfo FindField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var key = type.FullName + "|F|" + name;
            FieldInfo cached;
            if (FieldCache.TryGetValue(key, out cached)) return cached;
            FieldInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
                found = t.GetField(name, All);
            FieldCache[key] = found;
            return found;
        }

        public static PropertyInfo FindProperty(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var key = type.FullName + "|P|" + name;
            PropertyInfo cached;
            if (PropCache.TryGetValue(key, out cached)) return cached;
            PropertyInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType)
                found = t.GetProperty(name, All);
            PropCache[key] = found;
            return found;
        }

        /// <summary>Find a method by name and (optionally) parameter count; walks the base chain.</summary>
        public static MethodInfo FindMethod(Type type, string name, int argCount = -1)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            var key = type.FullName + "|M|" + name + "|" + argCount;
            MethodInfo cached;
            if (MethodCache.TryGetValue(key, out cached)) return cached;

            MethodInfo found = FindMethodShallow(type, name, argCount);
            for (var t = type.BaseType; t != null && found == null; t = t.BaseType)
                found = FindMethodShallow(t, name, argCount);
            MethodCache[key] = found;
            return found;
        }

        private static MethodInfo FindMethodShallow(Type type, string name, int argCount)
        {
            MethodInfo loose = null;
            var methods = type.GetMethods(All);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != name) continue;
                if (argCount < 0) return m;
                int n = m.GetParameters().Length;
                if (n == argCount) return m;
                if (n > argCount && loose == null) loose = m; // 允许带可选参数
            }
            return loose;
        }

        /// <summary>Try several member names in order (renamed between game versions).</summary>
        public static MethodInfo FindMethodAny(Type type, string[] names, int argCount = -1)
        {
            if (names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                var m = FindMethod(type, names[i], argCount);
                if (m != null) return m;
            }
            return null;
        }

        public static bool HasMethod(Type type, string name, int argCount = -1)
        {
            return FindMethod(type, name, argCount) != null;
        }

        /// <summary>任意一个候补名字存在即视为可用（版本间改名兼容）。</summary>
        public static bool HasMethodAny(Type type, string[] names, int argCount = -1)
        {
            return FindMethodAny(type, names, argCount) != null;
        }

        /// <summary>Find a constructor by parameter count.</summary>
        public static ConstructorInfo FindCtor(Type type, int argCount = -1)
        {
            if (type == null) return null;
            var key = type.FullName + "|C|" + argCount;
            ConstructorInfo cached;
            if (CtorCache.TryGetValue(key, out cached)) return cached;
            ConstructorInfo found = null;
            var ctors = type.GetConstructors(All);
            for (int i = 0; i < ctors.Length; i++)
            {
                if (argCount < 0) { found = ctors[i]; break; }
                if (ctors[i].GetParameters().Length == argCount) { found = ctors[i]; break; }
            }
            CtorCache[key] = found;
            return found;
        }

        public static bool HasCtor(Type type, int argCount)
        {
            return FindCtor(type, argCount) != null;
        }

        // ------------------------------------------------------------------ get / set / call

        public static object Get(object instance, string field)
        {
            if (instance == null) return null;
            var f = FindField(instance.GetType(), field);
            if (f == null) { WarnOnce("field:" + instance.GetType().Name + "." + field, "字段缺失 " + instance.GetType().Name + "." + field); return null; }
            try { return f.GetValue(instance); }
            catch (Exception e) { WarnOnce("get:" + instance.GetType().Name + "." + field, "读取失败 " + field + ": " + e.Message); return null; }
        }

        public static bool Set(object instance, string field, object value)
        {
            if (instance == null) return false;
            var f = FindField(instance.GetType(), field);
            if (f == null) { WarnOnce("field:" + instance.GetType().Name + "." + field, "字段缺失 " + instance.GetType().Name + "." + field); return false; }
            try { f.SetValue(instance, value); return true; }
            catch (Exception e) { WarnOnce("set:" + instance.GetType().Name + "." + field, "写入失败 " + field + ": " + e.Message); return false; }
        }

        /// <summary>Set an enum field from an int value (Mono/.NET both accept the boxed enum).</summary>
        public static bool SetEnum(object instance, string field, int value)
        {
            if (instance == null) return false;
            var f = FindField(instance.GetType(), field);
            if (f == null) { WarnOnce("field:" + instance.GetType().Name + "." + field, "字段缺失 " + instance.GetType().Name + "." + field); return false; }
            try
            {
                object boxed = f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, value) : (object)value;
                f.SetValue(instance, boxed);
                return true;
            }
            catch (Exception e) { WarnOnce("setEnum:" + field, "写枚举失败 " + field + ": " + e.Message); return false; }
        }

        public static object GetStatic(Type type, string field)
        {
            var f = FindField(type, field);
            if (f == null) return null;
            try { return f.GetValue(null); } catch (Exception) { return null; }
        }

        /// <summary>读静态属性（data-dev 的诊断字段常是 static 属性 getter）。</summary>
        public static object GetStaticProperty(Type type, string property)
        {
            var p = FindProperty(type, property);
            if (p == null || !p.CanRead) return null;
            try { return p.GetValue(null, null); }
            catch (Exception) { return null; }
        }

        /// <summary>先当静态属性读，读不到再当静态字段读（版本/写法差异兜底）。</summary>
        public static object GetStaticValue(Type type, string name)
        {
            var value = GetStaticProperty(type, name);
            if (value != null) return value;
            return GetStatic(type, name);
        }

        public static bool SetStatic(Type type, string field, object value)
        {
            var f = FindField(type, field);
            if (f == null) { WarnOnce("sfield:" + type.Name + "." + field, "静态字段缺失 " + type.Name + "." + field); return false; }
            try { f.SetValue(null, value); return true; }
            catch (Exception e) { WarnOnce("sset:" + field, "写静态字段失败 " + field + ": " + e.Message); return false; }
        }

        /// <summary>Call an instance method (by name), returns null on failure/void.</summary>
        public static object Call(object instance, string method, params object[] args)
        {
            string error;
            return TryCall(instance, method, args, out error);
        }

        public static object TryCall(object instance, string method, object[] args, out string error)
        {
            error = null;
            if (instance == null) { error = "instance null"; return null; }
            var m = FindMethod(instance.GetType(), method, args == null ? 0 : args.Length);
            if (m == null)
            {
                error = "方法缺失 " + instance.GetType().Name + "." + method;
                WarnOnce("method:" + instance.GetType().Name + "." + method, error);
                return null;
            }
            try { return m.Invoke(instance, args); }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                error = method + " 抛异常: " + inner.GetType().Name + ": " + inner.Message;
                WarnOnce("call:" + instance.GetType().Name + "." + method + "|" + inner.Message, error);
                return null;
            }
        }

        public static bool TryCallAny(object instance, string[] names, object[] args, out string usedName)
        {
            usedName = null;
            if (instance == null) return false;
            for (int i = 0; i < names.Length; i++)
            {
                var m = FindMethod(instance.GetType(), names[i], args == null ? 0 : args.Length);
                if (m == null) continue;
                try
                {
                    m.Invoke(instance, args);
                    usedName = names[i];
                    return true;
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    WarnOnce("callany:" + names[i] + "|" + inner.Message, names[i] + " 抛异常: " + inner.Message);
                }
            }
            return false;
        }

        public static object CallStatic(Type type, string method, params object[] args)
        {
            var m = FindMethod(type, method, args == null ? 0 : args.Length);
            if (m == null)
            {
                WarnOnce("smethod:" + type.Name + "." + method, "静态方法缺失 " + type.Name + "." + method);
                return null;
            }
            try { return m.Invoke(null, args); }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                WarnOnce("scall:" + type.Name + "." + method + "|" + inner.Message, method + " 抛异常: " + inner.Message);
                return null;
            }
        }

        public static bool TryCallStatic(Type type, string method, object[] args)
        {
            var m = FindMethod(type, method, args == null ? 0 : args.Length);
            if (m == null) { WarnOnce("smethod:" + type.Name + "." + method, "静态方法缺失 " + type.Name + "." + method); return false; }
            try { m.Invoke(null, args); return true; }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                WarnOnce("scall:" + type.Name + "." + method + "|" + inner.Message, method + " 抛异常: " + inner.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ conversions

        public static int ToInt(object o)
        {
            if (o == null) return 0;
            try
            {
                if (o is Enum) return Convert.ToInt32(o);
                return Convert.ToInt32(o);
            }
            catch (Exception) { return 0; }
        }

        public static bool ToBool(object o)
        {
            if (o == null) return false;
            try { return o is bool ? (bool)o : Convert.ToBoolean(o); }
            catch (Exception) { return false; }
        }

        public static string Name(object o)
        {
            if (o == null) return "null";
            try { return o.GetType().Name; } catch (Exception) { return "?"; }
        }

        public static void WarnOnce(string key, string message)
        {
            if (key == null) key = message;
            lock (Warned)
            {
                if (Warned.Contains(key)) return;
                Warned.Add(key);
            }
            Log.Warn("[nav] " + message);
        }

        public static void ResetWarnings()
        {
            lock (Warned) Warned.Clear();
        }
    }
}
