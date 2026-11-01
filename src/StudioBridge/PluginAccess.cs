using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;

namespace StudioMcp.Bridge
{
    // Names passed here are fixed by the KKPE/Timeline adapters, never supplied by an RPC client.
    internal static class PluginAccess
    {
        internal const string PoseEditorId = "com.joan6694.kkplugins.kkpe";
        internal const string TimelineId = "com.joan6694.illusionplugins.timeline";
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal static Type Type(string pluginId, string name)
        {
            BepInEx.PluginInfo info;
            return Chainloader.PluginInfos.TryGetValue(pluginId, out info) && info.Instance != null
                ? info.Instance.GetType().Assembly.GetType(name, false) : null;
        }

        internal static object Get(object target, string name)
        {
            var type = target as Type ?? target.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target is Type ? null : target);
                var property = current.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target is Type ? null : target, null);
            }
            throw new NotSupportedException(type.FullName + " has no compatible " + name + " member.");
        }

        internal static void Set(object target, string name, object value)
        {
            var type = target.GetType();
            var field = type.GetField(name, Flags);
            if (field != null) { field.SetValue(target, value); return; }
            var property = type.GetProperty(name, Flags);
            if (property != null && property.CanWrite) { property.SetValue(target, value, null); return; }
            throw new NotSupportedException(type.FullName + " has no compatible writable " + name + " member.");
        }

        internal static object Call(object target, string name, params object[] args)
        {
            var type = target as Type ?? target.GetType();
            var methods = type.GetMethods(Flags).Where(m => m.Name == name && !m.ContainsGenericParameters)
                .Where(m => m.GetParameters().Length == args.Length)
                .Where(m => m.GetParameters().Select((p, i) => args[i] == null
                    ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])).All(ok => ok)).ToArray();
            if (methods.Length != 1) throw new NotSupportedException(type.FullName + " has no unambiguous compatible " + name + " method.");
            return methods[0].Invoke(target is Type ? null : target, args);
        }
    }
}
