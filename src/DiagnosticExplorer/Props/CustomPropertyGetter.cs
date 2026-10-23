namespace DiagnosticExplorer;

/// <summary>
///     A getter for a property that exists only in configuration — it has no PropertyInfo behind
///     it, just a value function the host supplied.
/// </summary>
internal sealed class CustomPropertyGetter : PropertyGetter
{
    private readonly Func<object, string> _categoryFormatter;
    private readonly Func<object, string> _descriptionFormatter;
    private readonly ConfiguredValue<bool> _initiallyExpanded;

    public CustomPropertyGetter(CustomPropertyConfiguration configuration)
    {
        ConfigureCustomProperty(configuration);
        GetFunc = configuration.Value;
        _categoryFormatter = configuration.CategoryFormatter;
        _descriptionFormatter = configuration.DescriptionFormatter;
        _initiallyExpanded = configuration.InitiallyExpanded;
    }

    /// <summary>
    ///     False because there is no declared property behind this getter, which is what
    ///     <see cref="NestedPropertyRenderMode.PrimaryOnly" /> filters on.
    /// </summary>
    internal override bool IsDirectProperty => false;

    public override void GetProperties(object obj, PropertyBag bag, string catPrepend)
    {
        if (!_initiallyExpanded.IsSet || GetFunc(obj) is not IInlineCustomObject inlineCustomObject)
        {
            base.GetProperties(obj, bag, catPrepend);
            return;
        }

        string category = CombineCategories(catPrepend, GetName(obj));
        inlineCustomObject.AddProperties(bag, category);

        Category expandedCategory = bag.FindOrCreateCategory(category);
        expandedCategory.IsExpanded = _initiallyExpanded.Value;
        expandedCategory.IsExpandedProperty = true;
    }

    protected override string GetCategory(object obj)
    {
        return Format(_categoryFormatter, obj, base.GetCategory);
    }

    protected override string GetDescription(object obj)
    {
        return Format(_descriptionFormatter, obj, base.GetDescription);
    }

    /// <summary>
    ///     Runs a host-supplied formatter, falling back to the configured value when there is none.
    ///     A throwing formatter renders as its message rather than taking down the whole bag: one
    ///     bad lambda should cost one property, not every diagnostic on the process.
    /// </summary>
    private static string Format(Func<object, string> formatter, object obj, Func<object, string> fallback)
    {
        if (formatter == null)
        {
            return fallback(obj);
        }

        try
        {
            return formatter(obj);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }
}
