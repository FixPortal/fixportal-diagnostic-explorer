namespace DiagnosticExplorer;

public static class DatePropertyConfiguratorExtensions
{
    public static IPropertyConfigurator<T, DateTime> ShowDate<T>(
        this IPropertyConfigurator<T, DateTime> property,
        bool expose = true
    ) => ConfigureDate(property, exposeDate: expose);

    public static IPropertyConfigurator<T, DateTime?> ShowDate<T>(
        this IPropertyConfigurator<T, DateTime?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeDate: expose);

    public static IPropertyConfigurator<T, DateTimeOffset> ShowDate<T>(
        this IPropertyConfigurator<T, DateTimeOffset> property,
        bool expose = true
    ) => ConfigureDate(property, exposeDate: expose);

    public static IPropertyConfigurator<T, DateTimeOffset?> ShowDate<T>(
        this IPropertyConfigurator<T, DateTimeOffset?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeDate: expose);

    public static IPropertyConfigurator<T, DateTime> ShowElapsed<T>(
        this IPropertyConfigurator<T, DateTime> property,
        bool expose = true
    ) => ConfigureDate(property, exposeElapsed: expose);

    public static IPropertyConfigurator<T, DateTime?> ShowElapsed<T>(
        this IPropertyConfigurator<T, DateTime?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeElapsed: expose);

    public static IPropertyConfigurator<T, DateTimeOffset> ShowElapsed<T>(
        this IPropertyConfigurator<T, DateTimeOffset> property,
        bool expose = true
    ) => ConfigureDate(property, exposeElapsed: expose);

    public static IPropertyConfigurator<T, DateTimeOffset?> ShowElapsed<T>(
        this IPropertyConfigurator<T, DateTimeOffset?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeElapsed: expose);

    public static IPropertyConfigurator<T, DateTime> ShowTimeUntil<T>(
        this IPropertyConfigurator<T, DateTime> property,
        bool expose = true
    ) => ConfigureDate(property, exposeTimeUntil: expose);

    public static IPropertyConfigurator<T, DateTime?> ShowTimeUntil<T>(
        this IPropertyConfigurator<T, DateTime?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeTimeUntil: expose);

    public static IPropertyConfigurator<T, DateTimeOffset> ShowTimeUntil<T>(
        this IPropertyConfigurator<T, DateTimeOffset> property,
        bool expose = true
    ) => ConfigureDate(property, exposeTimeUntil: expose);

    public static IPropertyConfigurator<T, DateTimeOffset?> ShowTimeUntil<T>(
        this IPropertyConfigurator<T, DateTimeOffset?> property,
        bool expose = true
    ) => ConfigureDate(property, exposeTimeUntil: expose);

    private static IPropertyConfigurator<T, TDate> ConfigureDate<T, TDate>(
        IPropertyConfigurator<T, TDate> property,
        bool? exposeDate = null,
        bool? exposeElapsed = null,
        bool? exposeTimeUntil = null
    )
    {
        if (property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        if (property is IDateStrategyConfigurator configurator)
        {
            configurator.ConfigureDate(exposeDate, exposeElapsed, exposeTimeUntil);
        }

        return property;
    }
}
