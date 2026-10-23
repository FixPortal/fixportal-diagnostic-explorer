using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace DiagnosticExplorer;

[SuppressMessage(
    "Maintainability",
    "S4136:Method overloads should be grouped together",
    Justification = "Grouped by collection shape - array, List, IEnumerable - rather than by name, so "
        + "each shape's full fluent surface reads as one block. Regrouping by name would interleave "
        + "the shapes and make the file harder to follow."
)]
public static class CollectionPropertyConfiguratorExtensions
{
    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, TItem[]> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, List<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, HashSet<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, ObservableCollection<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, BindingList<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ListItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        Action<ICollectionListConfigurator<KeyValuePair<TKey, TValue>>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> CollectionItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ShowCount<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ShowCount(name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        string separator = null,
        Func<KeyValuePair<TKey, TValue>, string> format = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        Func<KeyValuePair<TKey, TValue>, string> format
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ExpandItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        Action<ICollectionExpandedItemConfigurator<KeyValuePair<TKey, TValue>>> configure = null,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> WithMaxItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, Dictionary<TKey, TValue>> property,
        int maxItems
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ListItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        Action<ICollectionListConfigurator<KeyValuePair<TKey, TValue>>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> CollectionItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ShowCount<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ShowCount(name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        string separator = null,
        Func<KeyValuePair<TKey, TValue>, string> format = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        Func<KeyValuePair<TKey, TValue>, string> format
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ExpandItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        Action<ICollectionExpandedItemConfigurator<KeyValuePair<TKey, TValue>>> configure = null,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> WithMaxItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IDictionary<TKey, TValue>> property,
        int maxItems
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ListItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        Action<ICollectionListConfigurator<KeyValuePair<TKey, TValue>>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> CollectionItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ShowCount<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ShowCount(name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        string separator = null,
        Func<KeyValuePair<TKey, TValue>, string> format = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ConcatItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        Func<KeyValuePair<TKey, TValue>, string> format
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> ExpandItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        Action<ICollectionExpandedItemConfigurator<KeyValuePair<TKey, TValue>>> configure = null,
        string name = null
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, KeyValuePair<TKey, TValue>> WithMaxItems<T, TKey, TValue>(
        this IPropertyConfigurator<T, IReadOnlyDictionary<TKey, TValue>> property,
        int maxItems
    ) => ConfigureCollection<T, KeyValuePair<TKey, TValue>>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, IList<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyList<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, IReadOnlyCollection<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => property.CollectionItems().ListItems(configure);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, ISet<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    public static ICollectionConfigurator<T, TItem> CollectionItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property
    ) => ConfigureCollection<T, TItem>(property);

    public static ICollectionConfigurator<T, TItem> ShowCount<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ShowCount(name);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        string separator = null,
        Func<TItem, string> format = null
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(separator, format);

    public static ICollectionConfigurator<T, TItem> ConcatItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        Func<TItem, string> format
    ) => ConfigureCollection<T, TItem>(property).ConcatItems(format);

    public static ICollectionConfigurator<T, TItem> ListItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        Action<ICollectionListConfigurator<TItem>> configure = null
    ) => ConfigureCollection<T, TItem>(property).ListItems(configure);

    public static ICollectionConfigurator<T, TItem> ExpandItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    ) => ConfigureCollection<T, TItem>(property).ExpandItems(configure, name);

    public static ICollectionConfigurator<T, TItem> WithMaxItems<T, TItem>(
        this IPropertyConfigurator<T, ICollection<TItem>> property,
        int maxItems
    ) => ConfigureCollection<T, TItem>(property).WithMaxItems(maxItems);

    private static ICollectionConfigurator<T, TItem> ConfigureCollection<T, TItem>(IPropertyConfigurator property)
    {
        if (property == null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        if (property is ICollectionStrategyConfigurator<T> configurator)
        {
            return configurator.ConfigureCollection<TItem>();
        }

        return new CollectionConfigurator<T, TItem>(
            new PropertyConfiguration(string.Empty, typeof(ICollection<TItem>), _ => null)
        );
    }
}
