namespace DiagnosticExplorer;

public static class ExtendedPropertyConfiguratorExtensions
{
    public static IPropertyConfigurator<T, TProperty> Expand<T, TProperty>(
        this IPropertyConfigurator<T, TProperty> property,
        bool initiallyExpanded = true
    )
    {
        if (property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        if (property is IExtendedStrategyConfigurator configurator)
        {
            configurator.ConfigureExtended(initiallyExpanded);
        }

        return property;
    }

    public static IPropertyConfigurator<T, TProperty> WithPrimaryPropertiesOnly<T, TProperty>(
        this IPropertyConfigurator<T, TProperty> property
    )
    {
        if (property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        if (property is IExtendedStrategyConfigurator configurator)
        {
            configurator.ConfigurePrimaryPropertiesOnly();
        }

        return property;
    }
}
