// Owns quiet, cached reflection over metadata supplied by optional recipe
// providers. Missing author-defined members are expected evidence gaps, so lookup
// never routes through Harmony's diagnostic AccessTools helpers and never logs.
using System;
using System.Collections.Generic;
using System.Reflection;

#nullable disable

namespace OC2MenuManager
{
    /// <summary>
    /// Resolves readable fields and properties across an optional provider type's
    /// inheritance chain. Positive and negative lookups are cached per runtime
    /// type, field names take precedence over property names, and value-access
    /// failures remain isolated from the core tracker.
    /// </summary>
    internal static class OptionalMemberAccessor
    {
        private const BindingFlags DeclaredMemberFlags = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> FieldsByType =
            new Dictionary<Type, Dictionary<string, FieldInfo>>();
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> PropertiesByType =
            new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        /// <summary>
        /// Reads a field or non-indexed property without emitting diagnostics when
        /// the member is absent or its getter fails. Static members are accepted to
        /// preserve provider metadata shapes previously handled by reflection.
        /// </summary>
        internal static object GetValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName))
            {
                return null;
            }

            FieldInfo field = ResolveField(instance.GetType(), memberName);
            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    return null;
                }
            }

            PropertyInfo property = ResolveProperty(instance.GetType(), memberName);
            if (property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Attempts to read an instance field or property while preserving the
        /// distinction between a missing member and a present member whose value is
        /// null. Static and indexed members are rejected.
        /// </summary>
        internal static bool TryGetInstanceValue(object instance, string memberName, out object value)
        {
            value = null;
            if (instance == null || string.IsNullOrEmpty(memberName))
            {
                return false;
            }

            Type type = instance.GetType();
            FieldInfo field = ResolveField(type, memberName);
            if (field != null && !field.IsStatic)
            {
                try
                {
                    value = field.GetValue(instance);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            PropertyInfo property = ResolveProperty(type, memberName);
            MethodInfo getter = property != null ? property.GetGetMethod(true) : null;
            if (getter == null || getter.IsStatic)
            {
                return false;
            }

            try
            {
                value = property.GetValue(instance, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static FieldInfo ResolveField(Type type, string memberName)
        {
            Dictionary<string, FieldInfo> members;
            if (!FieldsByType.TryGetValue(type, out members))
            {
                members = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                FieldsByType[type] = members;
            }

            FieldInfo field;
            if (members.TryGetValue(memberName, out field))
            {
                return field;
            }

            for (Type current = type; current != null; current = current.BaseType)
            {
                try
                {
                    field = current.GetField(memberName, DeclaredMemberFlags);
                }
                catch
                {
                    field = null;
                }

                if (field != null)
                {
                    break;
                }
            }

            members[memberName] = field;
            return field;
        }

        private static PropertyInfo ResolveProperty(Type type, string memberName)
        {
            Dictionary<string, PropertyInfo> members;
            if (!PropertiesByType.TryGetValue(type, out members))
            {
                members = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                PropertiesByType[type] = members;
            }

            PropertyInfo property;
            if (members.TryGetValue(memberName, out property))
            {
                return property;
            }

            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo[] declaredProperties;
                try
                {
                    declaredProperties = current.GetProperties(DeclaredMemberFlags);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < declaredProperties.Length; i++)
                {
                    PropertyInfo candidate = declaredProperties[i];
                    if (candidate != null
                        && candidate.CanRead
                        && candidate.GetIndexParameters().Length == 0
                        && string.Equals(candidate.Name, memberName, StringComparison.Ordinal))
                    {
                        property = candidate;
                        break;
                    }
                }

                if (property != null)
                {
                    break;
                }
            }

            members[memberName] = property;
            return property;
        }
    }
}
