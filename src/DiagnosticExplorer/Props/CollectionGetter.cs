using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace DiagnosticExplorer;

internal class CollectionGetter : PropertyGetter
{
    private readonly string _separator;
    private readonly CollectionMode _mode;
    private readonly int _maxItems;
    private readonly Func<object, object> _nameFunc;
    private readonly Func<object, object> _valueFunc;
    private readonly Func<object, object> _descrFunc;
    private readonly Func<object, object> _catFunc;
    private readonly Func<object, int, string> _indexedNameFormatter;
    private readonly bool _initiallyExpanded;
    private readonly NestedPropertyRenderMode _itemRenderMode;

    public CollectionGetter(PropertyInfo info, CollectionPropertyAttribute attr, bool isStatic)
        : this(info, attr.CreateOptions(), attr, null, isStatic) { }

    /// <summary>
    ///     False for ExpandedItems, which contributes categories rather than a property of its own.
    /// </summary>
    internal override bool IsDirectProperty => _mode != CollectionMode.ExpandedItems;

    /// <summary>
    ///     Driven by <see cref="CollectionOptions" /> rather than the attribute directly, so an
    ///     attribute-configured and a fluently-configured collection reach the same code path.
    /// </summary>
    internal CollectionGetter(
        PropertyInfo info,
        CollectionOptions options,
        DiagnosticPropertyAttribute metadata,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes = true,
        string defaultFormat = null
    )
        : base(info, metadata, configuration, isStatic, applyAttributes, defaultFormat)
    {
        CollectionOptions attr = options ?? new CollectionOptions(CollectionMode.Count);
        _separator = attr.Separator ?? Environment.NewLine;
        _mode = attr.Mode;

        // info is null for a configured delegate or direct-field property (BuildPropertyGetters
        // passes info: null, delegateProperty: configuration) -- fall back to the configured value
        // type so a collection-typed delegate/field property doesn't NRE on the very first render
        // and blank every property of the type it belongs to.
        Type collectionType = info?.PropertyType ?? configuration?.ValueType;
        Type genericType = GenericObjectCache.FindGenericInterface(collectionType, typeof(IDictionary<,>));
        bool isDictionary = typeof(IDictionary).IsAssignableFrom(collectionType);

        if (genericType != null)
        {
            IDictPropGetter propGetter = GenericObjectCache.CreateGenericObject<IDictPropGetter>(
                typeof(DictPropGetter<,>),
                genericType.GetGenericArguments()
            );
            _nameFunc = propGetter.GetNameGetter();
            _valueFunc = propGetter.GetValueGetter();
        }
        else if (isDictionary)
        {
            _nameFunc = x => ((DictionaryEntry)x).Key;
            _valueFunc = x => ((DictionaryEntry)x).Value;
        }
        else
        {
            _nameFunc = PropertyToFunction(GetListProperty(collectionType, attr.NameProperty), isStatic);
            _valueFunc = PropertyToFunction(GetListProperty(collectionType, attr.ValueProperty), isStatic);
            _descrFunc = PropertyToFunction(GetListProperty(collectionType, attr.DescriptionProperty), isStatic);
            _catFunc = PropertyToFunction(GetListProperty(collectionType, attr.CategoryProperty), isStatic);
        }

        // Configured formatter delegates take precedence over the reflected NameProperty/
        // ValueProperty/etc. above -- ListItems(x => x.WithName(...)) and ConcatItems(format)
        // were previously accepted and silently ignored because only the *Property options were
        // ever read here.
        if (attr.NameFormatter != null)
        {
            Func<object, string> nameFormatter = attr.NameFormatter;
            _nameFunc = x => nameFormatter(x);
        }
        _indexedNameFormatter = attr.IndexedNameFormatter;
        if (attr.ValueFormatter != null)
        {
            Func<object, string> valueFormatter = attr.ValueFormatter;
            _valueFunc = x => valueFormatter(x);
        }
        if (attr.DescriptionFormatter != null)
        {
            Func<object, string> descriptionFormatter = attr.DescriptionFormatter;
            _descrFunc = x => descriptionFormatter(x);
        }
        if (attr.CategoryFormatter != null)
        {
            Func<object, string> categoryFormatter = attr.CategoryFormatter;
            _catFunc = x => categoryFormatter(x);
        }

        _maxItems = attr.MaxItems;
        _initiallyExpanded = attr.InitiallyExpanded;
        _itemRenderMode = attr.PrimaryPropertiesOnly
            ? NestedPropertyRenderMode.PrimaryOnly
            : NestedPropertyRenderMode.All;
    }

    public interface IDictPropGetter
    {
        Func<object, object> GetNameGetter();
        Func<object, object> GetValueGetter();
    }

    public sealed class DictPropGetter<TKey, TValue> : IDictPropGetter
    {
        public Func<object, object> GetNameGetter()
        {
            return value => ((KeyValuePair<TKey, TValue>)value).Key;
        }

        public Func<object, object> GetValueGetter()
        {
            return value => ((KeyValuePair<TKey, TValue>)value).Value;
        }
    }

    private static PropertyInfo GetListProperty(Type collectionType, string name)
    {
        if (string.IsNullOrEmpty(name) || collectionType == null)
        {
            return null;
        }

        Type colType = null;
        if (collectionType.IsArray)
        {
            colType = collectionType.GetElementType();
        }
        else
        {
            Type enumerableType = GenericObjectCache.FindGenericInterface(collectionType, typeof(IEnumerable<>));
            if (enumerableType != null)
            {
                colType = enumerableType.GetGenericArguments()[0];
            }
        }

        if (colType == null)
        {
            return null;
        }

        PropertyInfo propInfo = colType.GetProperty(name, DiagnosticManager.PublicInstancePropertyFlags);

        if (propInfo == null)
        {
            Debug.WriteLine($"Diagnostics: Can't find property '{name}' on class '{colType}'");
        }

        return propInfo;
    }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "The collection modes share bounded enumeration and property projection."
    )]
    public override void GetProperties(object obj, PropertyBag bag, string catPrepend)
    {
        try
        {
            if (GetFunc(obj) is not IEnumerable rawCol)
            {
                bag.AddProperty(new Property(Name, null), PrependToCategory(catPrepend));
                return;
            }

            int count = -1;
            if (rawCol is ICollection c)
            {
                count = c.Count;
            }
            else
            {
                PropertyInfo countProp = rawCol
                    .GetType()
                    .GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (
                    countProp != null
                    && countProp.PropertyType == typeof(int)
                    && countProp.GetValue(rawCol) is int propertyCount
                )
                {
                    count = propertyCount;
                }
            }

            if (_mode == CollectionMode.Count && count != -1)
            {
                AddSummary(bag, catPrepend, FormatValue(count), rawCol, obj);
                return;
            }

            List<object> col = rawCol.Cast<object>().Take(10001).ToList();
            int actualCount = col.Count;
            bool wasTruncated = actualCount > 10000;
            if (wasTruncated)
            {
                col.RemoveAt(10000);
            }

            int displayCount = count != -1 ? count : col.Count;

            if (displayCount == 0)
            {
                AddSummary(bag, catPrepend, FormatValue(0), rawCol, obj);
                return;
            }

            switch (_mode)
            {
                case CollectionMode.Count:
                    string val = wasTruncated ? "10000+ items" : FormatValue(displayCount);
                    AddSummary(bag, catPrepend, val, rawCol, obj);
                    break;
                case CollectionMode.Concatenate:
                    AppendConcatenated(col, bag, catPrepend);
                    break;
                case CollectionMode.List:
                    AppendAllProperties(col, bag, catPrepend);
                    if (wasTruncated)
                    {
                        bag.AddProperty(new Property("...", "Truncated at 10000 items"), PrependToCategory(catPrepend));
                    }
                    break;
                case CollectionMode.Categories:
                    AppendSeparateCategories(col, bag, catPrepend);
                    if (wasTruncated)
                    {
                        bag.AddProperty(
                            new Property("...", "Truncated at 10000 items"),
                            CombineCategories(catPrepend, "...")
                        );
                    }
                    break;
                case CollectionMode.ExpandedItems:
                    AppendExpandedItems(col, bag, catPrepend, obj, wasTruncated);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported collection mode '{_mode}'.");
            }
        }
        catch (Exception ex)
        {
            string error = $"<{ex.Message}>";
            bag.AddProperty(new Property(Name, error), PrependToCategory(catPrepend));
        }
    }

    /// <summary>
    ///     Adds the one-line summary that stands in for the whole collection, carrying the
    ///     drilldown affordance for it.
    /// </summary>
    /// <remarks>
    ///     The summary is the only thing a Count-mode collection renders, so it is the only place a
    ///     drilldown into the collection can hang. Without this the configuration is accepted and
    ///     silently does nothing: the count still renders, so nothing looks broken, and the items
    ///     are simply unreachable. <paramref name="collection" /> rather than the count is what the
    ///     drilldown opens - the affordance has to name the collection itself.
    /// </remarks>
    private void AddSummary(PropertyBag bag, string catPrepend, string value, IEnumerable collection, object owner)
    {
        Property property = new(Name, value);
        ApplyDrillDown(property, collection, owner);
        bag.AddProperty(property, PrependToCategory(catPrepend));
    }

    /// <summary>
    ///     Renders the collection as one expanded category per item, each holding that item's own
    ///     properties inline rather than a nested value.
    /// </summary>
    /// <remarks>
    ///     Carries the same cycle and depth guards as <see cref="AppendSeparateCategories" />: this
    ///     mode recurses into arbitrary object graphs through
    ///     <see cref="NestedPropertyRenderer" />, so a self-referencing item would otherwise recurse
    ///     until the stack gives out. Upstream's equivalent has no such guard.
    /// </remarks>
    private void AppendExpandedItems(
        IEnumerable col,
        PropertyBag bag,
        string catPrepend,
        object owner,
        bool wasTruncated
    )
    {
        // An inline custom projection is already writing into the caller's category, so nesting
        // another level under the property name would bury it.
        bool isInlineProjection = owner is IInlineCustomObject;
        string category = isInlineProjection
            ? catPrepend
            : CombineCategories(PrependToCategory(catPrepend, owner), GetName(owner));

        int index = 0;
        HashSet<object> visited = DiagnosticManager.VisitedObjects;
        foreach (object listObject in col)
        {
            if (listObject == null)
            {
                continue;
            }

            string itemName = Convert.ToString(GetNextPropVal(listObject, _catFunc ?? _nameFunc, index++));
            string itemCategory = CombineCategories(category, itemName);

            if (TryAddRecursionGuardProperty(listObject, bag, itemCategory, visited))
            {
                continue;
            }

            visited.Add(listObject);
            try
            {
                NestedPropertyRenderer.Render(listObject, bag, itemCategory, _itemRenderMode);
            }
            finally
            {
                visited.Remove(listObject);
            }

            Category item = bag.Categories.FindByName(itemCategory);
            item?.ValueObject = listObject;
        }

        if (!isInlineProjection)
        {
            Category expandedCategory = bag.FindOrCreateCategory(category);
            expandedCategory.IsExpanded = _initiallyExpanded;
            expandedCategory.IsExpandedProperty = true;
        }

        if (wasTruncated)
        {
            bag.AddProperty(new Property("...", "Truncated at 10000 items"), CombineCategories(category, "..."));
        }
    }

    /// <summary>
    ///     Writes a placeholder and reports true when an item must not be recursed into, either
    ///     because it is already on the current path or because the walk is too deep.
    /// </summary>
    private static bool TryAddRecursionGuardProperty(
        object listObject,
        PropertyBag bag,
        string itemCategory,
        HashSet<object> visited
    )
    {
        string guard = null;
        if (visited.Contains(listObject))
        {
            guard = "<cycle>";
        }
        else if (visited.Count > 50)
        {
            guard = "<max depth>";
        }

        if (guard == null)
        {
            return false;
        }

        bag.AddProperty(new Property { Name = guard, SourceObject = listObject }, itemCategory);
        return true;
    }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "Cycle detection and recursive diagnostic projection are intentionally colocated."
    )]
    private void AppendSeparateCategories(IEnumerable col, PropertyBag bag, string catPrepend)
    {
        int index = 0;
        foreach (object listObject in col)
        {
            if (listObject == null)
            {
                continue;
            }

            var visited = DiagnosticManager.VisitedObjects;
            if (visited.Contains(listObject))
            {
                Property p = new Property
                {
                    Name = "<cycle>",
                    CanSet = false,
                    SourceObject = listObject,
                };
                string cycleCategory = $"Item {index++}";
                string cyclePrepend = CombineCategories(catPrepend, cycleCategory);
                bag.AddProperty(p, cyclePrepend);
                continue;
            }
            if (visited.Count > 50)
            {
                Property p = new Property
                {
                    Name = "<max depth>",
                    CanSet = false,
                    SourceObject = listObject,
                };
                string depthCategory = $"Item {index++}";
                string depthPrepend = CombineCategories(catPrepend, depthCategory);
                bag.AddProperty(p, depthPrepend);
                continue;
            }

            object catPropVal = GetNextPropVal(listObject, _catFunc ?? _nameFunc, index++);
            string valCategory = Convert.ToString(catPropVal);
            if (!string.IsNullOrEmpty(Category))
            {
                valCategory = Category.Contains('{')
                    ? string.Format(Category, catPropVal)
                    : $"{Category}.{valCategory}";
            }

            string newPrepend = CombineCategories(catPrepend, valCategory);

            visited.Add(listObject);
            try
            {
                foreach (PropertyGetter getter in DiagnosticManager.GetPropertyGetters(listObject))
                {
                    getter.GetProperties(listObject, bag, newPrepend);
                }

                if (bag.Categories.FindByName(newPrepend) is Category cat)
                {
                    cat.ValueObject = listObject;

                    // An item rendered as its own category is drillable in its own right: the
                    // category IS the object, so there is no property to hang the affordance on.
                    if (DrillDownEnabled && DiagnosticManager.IsDrillDownValue(listObject))
                    {
                        cat.CanDrillDown = true;
                        cat.DrillDownObject = listObject;
                        cat.DrillDownMaxItems = DrillDownMaxItems;
                    }
                }
            }
            finally
            {
                visited.Remove(listObject);
            }
        }
    }

    private void AppendAllProperties(IEnumerable col, PropertyBag bag, string catPrepend)
    {
        int index = 0;
        foreach (object obj in col)
        {
            object objectValue = obj;
            string name = Convert.ToString(ResolveItemName(obj, index++));
            string val = _valueFunc == null ? FormatValue(obj) : GetValue(obj, _valueFunc, out objectValue);

            string desc = _descrFunc == null ? null : GetValue(obj, _descrFunc, out _);
            string cat = _catFunc == null ? null : GetValue(obj, _catFunc, out _);

            Property prop = new Property(name, val, desc) { ValueObject = objectValue };
            bag.AddProperty(prop, CombineCategories(PrependToCategory(catPrepend), cat));
        }
    }

    private void AppendConcatenated(IEnumerable col, PropertyBag bag, string catPrepend)
    {
        if (_valueFunc != null)
        {
            col = col.Cast<object>()
                .Select(item =>
                {
                    try
                    {
                        return _valueFunc(item);
                    }
                    catch (Exception ex)
                    {
                        return $"<{ex.Message}>";
                    }
                });
        }

        string val = FormatEnumerable(col, _separator, _maxItems);
        bag.AddProperty(new Property(Name, val), PrependToCategory(catPrepend));
    }

    /// <summary>
    ///     Resolves an item's display name, preferring a configured <c>IndexedNameFormatter</c> (it
    ///     is documented to take precedence over the plain <c>NameFormatter</c>/<c>NameProperty</c>
    ///     because it is the only option that can see the item's position).
    /// </summary>
    private object ResolveItemName(object obj, int index)
    {
        if (_indexedNameFormatter == null)
        {
            return GetNextPropVal(obj, _nameFunc, index);
        }

        try
        {
            return _indexedNameFormatter(obj, index);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }

    private object GetNextPropVal(object obj, Func<object, object> propFunc, int index)
    {
        if (propFunc == null)
        {
            return $"{Name} {index}";
        }

        try
        {
            return propFunc(obj);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }
}
