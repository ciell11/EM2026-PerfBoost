using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace EM2026PerfBoost
{
    /// <summary>
    /// Dynamically locates and tracks the active EMSkinCore assembly generation loaded by iamgri mod's HostPlugin.
    /// Never takes a compile-time dependency on EMSkinCore.dll so iamgri updates and hot-reloads stay 100% compatible.
    /// </summary>
    internal static class CoreBridge
    {
        private static float _nextPollTime;
        private static float _nextDiscoveryTime;
        private static FieldInfo _loaderTickField;
        private static Assembly _activeCore;
        private static readonly Dictionary<string, Type> _typeCache = new();
        private static readonly Dictionary<(Type, string), FieldInfo> _fieldCache = new();

        public static Assembly ActiveCore => _activeCore;

        public static bool Poll(out Assembly newCore)
        {
            newCore = null;
            float now = Time.unscaledTime;
            if (_activeCore != null && now < _nextPollTime)
                return false;
            _nextPollTime = now + 1.0f;

            Assembly found = ResolveActiveCore();
            if (found != null && !ReferenceEquals(found, _activeCore))
            {
                _activeCore = found;
                _typeCache.Clear();
                _fieldCache.Clear();
                newCore = found;
                return true;
            }

            return false;
        }

        private static Assembly ResolveActiveCore()
        {
            // Fast path: read cached EMSkin.Loader._tick delegate's declaring assembly
            if (_loaderTickField != null)
            {
                try
                {
                    if (_loaderTickField.GetValue(null) is Delegate del)
                    {
                        var asm = del.Method?.DeclaringType?.Assembly;
                        if (asm != null)
                            return asm;
                    }
                }
                catch
                {
                    _loaderTickField = null;
                }
            }

            // Slow path: scan AppDomain at most once per second until discovered
            float now = Time.unscaledTime;
            if (now < _nextDiscoveryTime)
                return _activeCore;
            _nextDiscoveryTime = now + 1.0f;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    var asm = assemblies[i];
                    var loaderType = asm.GetType("Loader", false);
                    if (loaderType == null) continue;

                    var tickField = loaderType.GetField("_tick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (tickField == null) continue;

                    _loaderTickField = tickField;
                    if (tickField.GetValue(null) is Delegate del)
                    {
                        var coreAsm = del.Method?.DeclaringType?.Assembly;
                        if (coreAsm != null)
                            return coreAsm;
                    }
                }

                // Fallback: look directly for EMSkinCore by assembly name
                for (int i = assemblies.Length - 1; i >= 0; i--)
                {
                    var name = assemblies[i].GetName().Name;
                    if (string.Equals(name, "EMSkinCore", StringComparison.OrdinalIgnoreCase))
                        return assemblies[i];
                }
            }
            catch
            {
                // Ignore transient reflection issues during load
            }

            return _activeCore;
        }

        public static Type GetCoreType(string typeName)
        {
            if (_activeCore == null) return null;
            if (_typeCache.TryGetValue(typeName, out var cached))
                return cached;

            var t = _activeCore.GetType(typeName, false);
            _typeCache[typeName] = t;
            return t;
        }

        public static FieldInfo GetStaticField(Type type, string fieldName)
        {
            if (type == null) return null;
            var key = (type, fieldName);
            if (_fieldCache.TryGetValue(key, out var cached))
                return cached;

            var f = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _fieldCache[key] = f;
            return f;
        }

        public static bool IsCorePageRootAlive(string pageTypeName, string rootFieldName = "_root")
        {
            var t = GetCoreType(pageTypeName);
            if (t == null) return false;
            var f = GetStaticField(t, rootFieldName);
            if (f == null) return false;

            try
            {
                var val = f.GetValue(null);
                if (val is UnityEngine.Object uo)
                    return uo != null;
                return val != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
