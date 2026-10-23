using System.Linq.Expressions;
using System.Reflection;

namespace DiagnosticExplorer;

internal sealed class InlineCustomObjectConfigurator<T> : ICustomObjectConfigurator<T>
{
    private readonly List<IInlineCustomObjectMember> _members = [];

    public IReadOnlyList<IInlineCustomObjectMember> Members => _members;

    public IPropertyConfigurator<T, TProperty> Property<TProperty>(Expression<Func<T, TProperty>> property)
    {
        if (ExpressionProperty.TryGetDirectField(property, typeof(T), out FieldInfo field))
        {
            return ConfigureProperty(field.Name, property.Compile());
        }

        string name = ExpressionProperty.Get(property, typeof(T)).Name;
        return ConfigureProperty(name, property.Compile());
    }

    public IPropertyConfigurator<T, TProperty> Property<TProperty>(string name, Func<T, TProperty> value) =>
        ConfigureProperty(name, value);

    private PropertyConfiguration AddConfiguredProperty(
        string name,
        Type valueType,
        Func<object, object> value,
        PropertyStrategy strategy
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A property name is required.", nameof(name));
        }

        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        PropertyConfiguration configuration = new(name, valueType, value) { Strategy = strategy };
        _members.Add(new InlineConfiguredPropertyMember(configuration));
        return configuration;
    }

    private IPropertyConfigurator<T, TProperty> ConfigureProperty<TProperty>(string name, Func<T, TProperty> value)
    {
        PropertyConfiguration configuration = AddConfiguredProperty(
            name,
            typeof(TProperty),
            item => value((T)item),
            PropertyStrategy.Default
        );
        return new PropertyConfigurator<T, TProperty>(configuration);
    }
}

internal sealed class InlineCustomObject<T> : IInlineCustomObject
{
    private readonly T _source;
    private readonly IReadOnlyList<IInlineCustomObjectMember> _members;

    public InlineCustomObject(T source, IReadOnlyList<IInlineCustomObjectMember> members)
    {
        _source = source;
        _members = members;
    }

    public void AddProperties(PropertyBag bag)
    {
        AddProperties(bag, null);
    }

    public void AddProperties(PropertyBag bag, string category)
    {
        foreach (IInlineCustomObjectMember member in _members)
        {
            member.AddProperties(_source, this, bag, category);
        }
    }
}

/// <summary>One projected member of an inline custom object.</summary>
/// <remarks>
///     An interface rather than upstream's abstract class: it carries no state and no shared
///     behaviour, so the base class bought nothing.
/// </remarks>
internal interface IInlineCustomObjectMember
{
    void AddProperties(object source, object projection, PropertyBag bag, string category);
}

internal sealed class InlineConfiguredPropertyMember : IInlineCustomObjectMember
{
    private readonly PropertyConfiguration _configuration;

    public InlineConfiguredPropertyMember(PropertyConfiguration configuration) => _configuration = configuration;

    public void AddProperties(object source, object projection, PropertyBag bag, string category)
    {
        List<PropertyGetter> getters = [];
        DiagnosticManager.AddPropertyGetters(getters, null, null, _configuration.Bind(source), false, false, null);
        foreach (PropertyGetter getter in getters)
        {
            getter.GetProperties(projection, bag, category);
        }
    }
}
