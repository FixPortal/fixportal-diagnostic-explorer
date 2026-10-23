namespace DiagnosticExplorer;

public static class RatePropertyConfiguratorExtensions
{
    public static IPropertyConfigurator<T, RateCounter> ShowRate<T>(
        this IPropertyConfigurator<T, RateCounter> property,
        bool expose = true
    ) => ConfigureRate(property, exposeRate: expose);

    public static IPropertyConfigurator<T, RateCounter> ShowTotal<T>(
        this IPropertyConfigurator<T, RateCounter> property,
        bool expose = true
    ) => ConfigureRate(property, exposeTotal: expose);

    private static IPropertyConfigurator<T, RateCounter> ConfigureRate<T>(
        IPropertyConfigurator<T, RateCounter> property,
        bool? exposeRate = null,
        bool? exposeTotal = null
    )
    {
        if (property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        if (property is IRateStrategyConfigurator configurator)
        {
            configurator.ConfigureRate(exposeRate, exposeTotal);
        }

        return property;
    }
}
