namespace DiagnosticExplorer;

internal static class Guard
{
    public static void NotNull(object value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
    }
}
