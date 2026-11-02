using System;
using System.Linq;
using System.Reflection;

namespace SpaceEngineers.HighFPSFix
{
    // Name-based lookups of game members. Every miss throws with the full name,
    // so that the feature asking for it is switched off with a precise reason.
    internal static class GameReflection
    {
        public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        public static Assembly Assembly(string name)
        {
            Assembly found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            return found ?? throw new DetourException($"assembly {name} is not loaded");
        }

        public static Type Type(Assembly assembly, string fullName) =>
            assembly.GetType(fullName, false) ?? throw new DetourException($"type {fullName} not found in {assembly.GetName().Name}");

        public static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            MethodInfo method = type.GetMethods(All).FirstOrDefault(m =>
                m.Name == name && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
            return method ?? throw new DetourException($"method {type.FullName}::{name}({string.Join(", ", parameters.Select(p => p.Name))}) not found");
        }

        public static FieldInfo Field(Type type, string name)
        {
            FieldInfo field = type.GetField(name, All);
            return field ?? throw new DetourException($"field {type.FullName}::{name} not found");
        }

        public static PropertyInfo Property(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, All);
            return property ?? throw new DetourException($"property {type.FullName}::{name} not found");
        }
    }
}
