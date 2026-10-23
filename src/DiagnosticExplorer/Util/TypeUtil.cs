using System.Text.RegularExpressions;

namespace DiagnosticExplorer.Util;

internal static class TypeUtil
{
    public static string GetFriendlyTypeName(Type t)
    {
        if (!t.IsGenericType)
        {
            return ConvertTypeName(t);
        }

        var genericDef = t.GetGenericTypeDefinition();

        if (genericDef == typeof(Nullable<>))
        {
            return ConvertTypeName(t.GetGenericArguments()[0]) + "?";
        }

        var name = Regex.Replace(genericDef.Name, "`[0-9]+", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        var typeNames = t.GetGenericArguments().Select(GetFriendlyTypeName).ToArray();
        return string.Format("{0}<{1}>", name, string.Join(", ", typeNames));
    }

    /// <summary>
    ///     Returns true if type T is nullable
    /// </summary>
    /// <exception cref="ArgumentNullException">if t is Null</exception>
    public static bool IsNullable(Type t)
    {
        Guard.NotNull(t, nameof(t));

        return t.IsValueType && t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>);
    }

    private static string ConvertTypeName(Type type)
    {
        if (type == typeof(void))
        {
            return "void";
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(sbyte))
        {
            return "sbyte";
        }

        if (type == typeof(short))
        {
            return "short";
        }

        if (type == typeof(int))
        {
            return "int";
        }

        if (type == typeof(long))
        {
            return "long";
        }

        if (type == typeof(byte))
        {
            return "byte";
        }

        if (type == typeof(ushort))
        {
            return "ushort";
        }

        if (type == typeof(uint))
        {
            return "uint";
        }

        if (type == typeof(ulong))
        {
            return "ulong";
        }

        if (type == typeof(float))
        {
            return "float";
        }

        if (type == typeof(decimal))
        {
            return "decimal";
        }

        if (type == typeof(double))
        {
            return "double";
        }

        return type.Name;
    }
}
