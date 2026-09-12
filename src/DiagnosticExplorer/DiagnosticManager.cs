using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DiagnosticExplorer.Logging;
using DiagnosticExplorer.Util;

namespace DiagnosticExplorer;

/// <summary>
///     Which set of type configuration a render is reading.
/// </summary>
/// <remarks>
///     A type can be configured twice: once for how it appears in the main view, and again, through
///     <c>ConfigureDrillDown</c>, for how it appears when opened on its own. The second is normally
///     the fuller of the two — a summary line in a list, the whole object in the popup — so the
///     same object produces different properties depending on which view asked.
/// </remarks>
internal enum DiagnosticRenderMode
{
    Normal,
    DrillDown,
}

public static class DiagnosticManager
{
    private static readonly StringComparer _ignoreCase = StringComparer.CurrentCultureIgnoreCase;
    private static List<RegisteredObject> RegisteredObjects { get; set; }

    private static readonly ConcurrentDictionary<string, List<PropertyGetter>> _typeHash = new();

    private static readonly ConcurrentDictionary<Type, Lazy<OperationSet>> _operationLookup = new();
    private static readonly ConcurrentDictionary<Type, Lazy<OperationSet>> _staticOperationLookup = new();
    private static int _operationSetId;
    public static bool Enabled { get; set; } = true;

    private static DiagnosticConfigurationSnapshot _configuration = DiagnosticConfigurationSnapshot.Empty;

    /// <summary>
    ///     Bumped by every <see cref="UseConfiguration" /> and stamped into the getter cache key, so
    ///     entries built under a superseded configuration can never be read back.
    /// </summary>
    private static int _configurationVersion;

    /// <summary>
    ///     The render mode in force for the current bag, read by the getter pipeline.
    /// </summary>
    /// <remarks>
    ///     Ambient rather than a parameter because the pipeline recurses through
    ///     <see cref="GetPropertyGetters" /> from three getter classes — a collection's items, an
    ///     extended property's nested object, a nested renderer's — none of which is reached from
    ///     here directly. Threading the mode explicitly would mean putting it on every getter's
    ///     signature to serve one call at the bottom.
    ///     <para>
    ///         <see cref="AsyncLocal{T}" /> rather than <c>ThreadStatic</c> so a render entered on
    ///         one thread-pool thread keeps reading its own mode if that thread is later reused by
    ///         an unrelated render, not because any getter here actually awaits — every
    ///         <see cref="PropertyGetter.GetProperties" /> override is synchronous. If that ever
    ///         changes, note that cycle/depth detection (<see cref="VisitedObjects" />) stays
    ///         <c>[ThreadStatic]</c> deliberately (see its own remarks) and would need the same
    ///         treatment to keep working across a resumption on a different thread.
    ///     </para>
    /// </remarks>
    private static readonly AsyncLocal<DiagnosticRenderMode?> _renderMode = new();

    /// <summary>The configuration currently in force. Replaced wholesale by <see cref="UseConfiguration" />.</summary>
    public static DiagnosticConfiguration CurrentConfiguration { get; private set; } = new();

    /// <summary>Builds a configuration fluently and applies it.</summary>
    public static DiagnosticConfiguration Configure(Action<IDiagConfigurator> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        DiagnosticConfiguration configuration = new();
        configure(configuration);
        UseConfiguration(configuration);
        return configuration;
    }

    /// <summary>
    ///     Applies a configuration, replacing whatever was in force.
    /// </summary>
    /// <remarks>
    ///     Clearing the getter cache is not optional: getters are built once per type and bake the
    ///     configuration in, so a reconfiguration that left the cache alone would apply to types
    ///     nothing had touched yet and silently not to the rest.
    /// </remarks>
    public static void UseConfiguration(DiagnosticConfiguration configuration)
    {
        if (configuration == null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        EventRetentionOptions eventRetention = configuration.RuntimeOptions.EventRetention.CloneAndValidate();
        LogEventRetentionOptions logEventRetention = configuration.RuntimeOptions.LogEventRetention.CloneAndValidate();
        LogStreamRoutingConfiguration routing = configuration.RuntimeOptions.Routing.CreateSnapshot();
        DiagnosticConfigurationSnapshot snapshot = configuration.CreateSnapshot();

        // Configure the store before publishing the new configuration. A conflicting live router
        // leaves the old routing and every other configuration setting intact.
        LogEventStore.Configure(logEventRetention, routing);
        EventSinkRepo.Default.ConfigureEventRetention(eventRetention);

        CurrentConfiguration = configuration;
        _configuration = snapshot;

        // Only when the configuration actually says so. Enabled is a public, directly-settable
        // toggle, so assigning it unconditionally would silently switch diagnostics back on for a
        // host that had turned them off and then reconfigured something unrelated.
        if (configuration.RuntimeOptions.EnabledIsSet)
        {
            Enabled = configuration.RuntimeOptions.Enabled;
        }

        Interlocked.Increment(ref _configurationVersion);
        _typeHash.Clear();
    }

    /// <summary>
    ///     How many items a drill-down materialises before truncating, where nothing nearer to the
    ///     value said otherwise.
    /// </summary>
    internal static int DrillDownMaxItems => _configuration.DrillDownMaxItems;

    /// <summary>
    ///     The ceiling on a drilldown path chain.
    /// </summary>
    /// <remarks>
    ///     The chain arrives from the browser and each hop costs a full render of the previous
    ///     hop's value, so an unbounded chain is unbounded work on the agent for one request. Deep
    ///     enough that no operator reaches it by clicking.
    /// </remarks>
    internal const int MaxDrillDownDepth = 16;

    /// <summary>
    ///     The ceiling on a JSON hover payload.
    /// </summary>
    /// <remarks>
    ///     Serialising a live object graph produces however much text the graph happens to hold,
    ///     and the result crosses the same hub as everything else, against a 10 MB receive cap. A
    ///     payload over the ceiling is refused rather than truncated: half a JSON document is not
    ///     JSON, and a client that tried to parse it would report a syntax error instead of the
    ///     size problem.
    /// </remarks>
    internal const int MaxJsonHoverLength = 512 * 1024;

    /// <summary>
    ///     Whether a value is worth drilling into, or is a leaf best rendered in place. Strings and
    ///     scalars are leaves; so are UI elements, whose property graphs are enormous and circular.
    /// </summary>
    internal static bool IsDrillDownValue(object value)
    {
        if (value == null || value is string)
        {
            return false;
        }

        Type type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (
            type.IsPrimitive
            || type.IsEnum
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
        )
        {
            return false;
        }

        return !IsUserInterfaceElement(type);
    }

    /// <summary>
    ///     Matched by full name rather than by reference, so the core library keeps no dependency on
    ///     WinForms or WPF and this stays correct on every target framework.
    /// </summary>
    private static bool IsUserInterfaceElement(Type type)
    {
        for (Type baseType = type; baseType != null; baseType = baseType.BaseType)
        {
            if (
                baseType.FullName == "System.Windows.Forms.Control"
                || baseType.FullName == "System.Windows.FrameworkElement"
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The process-wide log stream. Logging-framework adapters publish here when constructed
    ///     without an explicit store, so a host that configures none still gets a usable stream.
    /// </summary>
    public static LogEventStore LogEventStore { get; } = new();

    static DiagnosticManager()
    {
        RegisteredObjects = [];
    }

    internal static void Clear()
    {
        _operationLookup.Clear();
        _staticOperationLookup.Clear();
        _typeHash.Clear();
        lock (RegisteredObjects)
        {
            RegisteredObjects.Clear();
        }
    }

    public static void Register(object o, string bagName, string bagCategory)
    {
        if (!Enabled)
        {
            return;
        }

        lock (RegisteredObjects)
        {
            RegisteredObject existing = RegisteredObjects.Find(ro => ReferenceEquals(ro.Object, o));

            bagName = MakeNameUnique(existing, bagName, bagCategory);
            if (existing == null)
            {
                RegisteredObjects.Add(new RegisteredObject(o, bagCategory, bagName));
            }
            else
            {
                existing.BagName = bagName;
                existing.BagCategory = bagCategory;
            }
        }
    }

    [SuppressMessage(
        "Reliability",
        "S1994:For loop increment clauses should modify the loop counters",
        Justification = "The unbounded suffix search always returns at the first available name."
    )]
    private static string MakeNameUnique(RegisteredObject obj, string name, string category)
    {
        if (name == null)
        {
            // ReSharper disable once ExpressionIsAlwaysNull -- legacy callers may supply null.
            return name;
        }

        var takenNames = new HashSet<string>(_ignoreCase);
        foreach (
            var ro in RegisteredObjects.Where(ro =>
                !ReferenceEquals(ro, obj) && _ignoreCase.Equals(category, ro.BagCategory)
            )
        )
        {
            takenNames.Add(ro.BagName);
        }

        if (!takenNames.Contains(name))
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string extension = $" {i}";
            string newName = $"{name}{extension}";
            if (!takenNames.Contains(newName))
            {
                return newName;
            }
        }
    }

    public static void Unregister(object obj)
    {
        lock (RegisteredObjects)
        {
            RegisteredObject existing = RegisteredObjects.Find(ro => ReferenceEquals(ro.Object, obj));

            if (existing != null)
            {
                RegisteredObjects.Remove(existing);
            }
        }
    }

    public static DiagnosticResponse GetDiagnostics()
    {
        return GetDiagnostics(GetRegisteredObjects());
    }

    public static DiagnosticResponse GetDiagnostics(IEnumerable<RegisteredObject> registeredObjects)
    {
        return GetDiagnostics(registeredObjects, DiagnosticRenderMode.Normal);
    }

    private static DiagnosticResponse GetDiagnostics(
        IEnumerable<RegisteredObject> registeredObjects,
        DiagnosticRenderMode renderMode
    )
    {
        try
        {
            DiagnosticResponse response = new();

            response.PropertyBags.AddRange(
                registeredObjects.Select(x => ObjectToPropertyBag(x.Object, x.BagName, x.BagCategory, renderMode))
            );

            HashSet<OperationSet> operationSets = [];

            foreach (PropertyBag bag in response.PropertyBags)
            {
                OperationSet bagOperations = GetOperationSet(bag.SourceObject);
                if (bagOperations != null)
                {
                    bag.OperationSet = bagOperations.Id;
                    operationSets.Add(bagOperations);
                }

                foreach (Category cat in bag.Categories)
                {
                    OperationSet catOperations = GetOperationSet(cat.ValueObject);
                    if (catOperations != null)
                    {
                        cat.OperationSet = catOperations.Id;
                        operationSets.Add(catOperations);
                    }
                }

                foreach (Property prop in bag.Categories.SelectMany(x => x.Properties))
                {
                    OperationSet propOperations = GetOperationSet(prop.ValueObject);
                    if (propOperations != null)
                    {
                        prop.OperationSet = propOperations.Id;
                        operationSets.Add(propOperations);
                    }
                }
            }
            response.OperationSets.AddRange(operationSets);

            return response;
        }
        catch (Exception ex)
        {
            return new DiagnosticResponse { ExceptionMessage = ex.Message, ExceptionDetail = ex.ToString() };
        }
    }

    private static OperationSet GetOperationSet(object sourceObject)
    {
        if (sourceObject == null)
        {
            return null;
        }

        if (sourceObject is Type type)
        {
            Lazy<OperationSet> lazy = _staticOperationLookup.GetOrAdd(
                type,
                t => new Lazy<OperationSet>(() => BuildStaticOperationSet(t))
            );
            return lazy.Value;
        }

        Type propType = sourceObject.GetType();
        Lazy<OperationSet> lazyInstance = _operationLookup.GetOrAdd(
            propType,
            t => new Lazy<OperationSet>(() => BuildOperationSet(t))
        );
        return lazyInstance.Value;
    }

    private static OperationSet BuildOperationSet(Type propType)
    {
        OperationSet operationSet = CreateOperationSet(propType);
        operationSet?.Id = Interlocked.Increment(ref _operationSetId).ToString();

        return operationSet;
    }

    private static OperationSet BuildStaticOperationSet(Type propType)
    {
        OperationSet operationSet = CreateStaticOperationSet(propType);
        operationSet?.Id = Interlocked.Increment(ref _operationSetId).ToString();

        return operationSet;
    }

    private static OperationSet CreateOperationSet(Type propType)
    {
        Guard.NotNull(propType, nameof(propType));

        if (propType.FullName == null)
        {
            return null;
        }

        if (propType.FullName.StartsWith("System."))
        {
            return null;
        }

        OperationSet operationSet = new();

        foreach (
            MethodInfo method in propType
                .GetMethods(PublicMethods)
                .Where(IsMethodValidOperationTarget)
                .OrderBy(x => x.Name)
        )
        {
            operationSet.Operations.Add(new Operation(method));
        }

        return operationSet.Operations.Count == 0 ? null : operationSet;
    }

    private static OperationSet CreateStaticOperationSet(Type propType)
    {
        Guard.NotNull(propType, nameof(propType));

        if (propType.FullName == null)
        {
            return null;
        }

        if (propType.FullName.StartsWith("System."))
        {
            return null;
        }

        OperationSet operationSet = new();

        foreach (
            MethodInfo method in propType
                .GetMethods(PublicStaticMethods)
                .Where(IsMethodValidOperationTarget)
                .OrderBy(x => x.Name)
        )
        {
            operationSet.Operations.Add(new Operation(method));
        }

        return operationSet.Operations.Count == 0 ? null : operationSet;
    }

    /// <summary>
    /// To be a valid operation target, a method must contain no ref/out parameters,
    /// no generic parameters apart from Nullable, and the must be allowed either by the DiagnosticClassAttribute
    /// or DiagnosticMethodAttribute
    /// </summary>
    private static bool IsMethodValidOperationTarget(MethodInfo method)
    {
        if (method.IsSpecialName)
        {
            return false;
        }

        if (method.IsGenericMethod)
        {
            return false;
        }

        if (method.GetParameters().Any(x => x.IsOut))
        {
            return false;
        }

        if (method.GetParameters().Any(x => x.ParameterType.IsByRef))
        {
            return false;
        }

        return AttributeUtil.GetAttribute<DiagnosticMethodAttribute>(method) != null;
    }

    public static RegisteredObject[] GetRegisteredObjects()
    {
        return GetRegisteredObjects(null);
    }

    /// <summary>Gets the configured and legacy diagnostic roots.</summary>
    /// <param name="serviceProvider">
    ///     The DI provider for configured <c>RegisterService</c> callbacks. <see langword="null" />
    ///     is supported for callbacks that use explicit <c>Register</c> roots.
    /// </param>
    public static RegisteredObject[] GetRegisteredObjects(IServiceProvider serviceProvider)
    {
        List<RegisteredObject> list =
        [
            .. _configuration
                .FindRegisteredObjects(serviceProvider)
                .Where(registeredObject => registeredObject?.Object != null),
        ];

        lock (RegisteredObjects)
        {
            for (int i = RegisteredObjects.Count - 1; i >= 0; i--)
            {
                RegisteredObject obj = RegisteredObjects[i];
                if (obj.Object == null)
                {
                    RegisteredObjects.RemoveAt(i);
                }
                else
                {
                    list.Add(obj);
                }
            }
        }
        return [.. list];
    }

    [ThreadStatic]
    private static HashSet<object> _visitedObjects;

    internal static HashSet<object> VisitedObjects =>
        _visitedObjects ??= new HashSet<object>(new ReferenceEqualityComparer());

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    public static PropertyBag ObjectToPropertyBag(object obj, string bagName, string bagCategory)
    {
        return ObjectToPropertyBag(obj, bagName, bagCategory, DiagnosticRenderMode.Normal);
    }

    private static PropertyBag ObjectToPropertyBag(
        object obj,
        string bagName,
        string bagCategory,
        DiagnosticRenderMode renderMode
    )
    {
        PropertyBag bag = new()
        {
            Name = bagName,
            Category = bagCategory,
            SourceObject = obj,
        };

        DiagnosticRenderMode? previousMode = _renderMode.Value;
        _renderMode.Value = renderMode;
        var visited = VisitedObjects;
        visited.Clear();
        try
        {
            if (obj != null)
            {
                visited.Add(obj);
                bag.CanDrillDown =
                    renderMode == DiagnosticRenderMode.Normal
                    && obj is not Type
                    && !IsUserInterfaceElement(obj.GetType())
                    && _configuration.HasDrillDownConfiguration(obj.GetType());
            }

            List<PropertyGetter> valueGetters = GetPropertyGetters(obj);

            foreach (PropertyGetter getter in valueGetters)
            {
                // ReSharper disable once AssignNullToNotNullAttribute -- null means no category prefix.
                getter.GetProperties(obj, bag, null);
            }
        }
        finally
        {
            visited.Clear();
            _renderMode.Value = previousMode;
        }

        return bag;
    }

    public const BindingFlags PublicInstancePropertyFlags =
        BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.Instance;
    private const BindingFlags PublicStaticPropertyFlags =
        BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.Static;
    private const BindingFlags PublicMethods = BindingFlags.Public | BindingFlags.InvokeMethod | BindingFlags.Instance;
    private const BindingFlags PublicStaticMethods =
        BindingFlags.Static | BindingFlags.InvokeMethod | BindingFlags.Public;

    internal static List<PropertyGetter> GetPropertyGetters(object obj)
    {
        if (obj == null)
        {
            return [];
        }

        Type type = obj.GetType();
        string typeKey = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        if (obj is Type t)
        {
            type = t;
            typeKey = "Static: " + (type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        }

        // ConcurrentDictionary.GetOrAdd makes the cache thread-safe (the build path runs
        // on the thread pool via the hub adapter). The factory is idempotent; a redundant
        // build under contention is harmless because only one list is stored.
        //
        // The key carries the configuration version because clearing the cache is not enough on
        // its own: a thread already inside GetOrAdd with the previous snapshot can insert its
        // getters just after UseConfiguration clears, leaving that type stale until the next
        // reconfiguration. Stamping the version means such a write lands under the old key and is
        // simply never read again.
        // The render mode is part of the key for the same reason the version is: a drilldown reads
        // a different type configuration, so it produces a different getter list for the same type.
        // One key would hand whichever view ran first to the other.
        int version = Volatile.Read(ref _configurationVersion);
        DiagnosticRenderMode renderMode = _renderMode.Value ?? DiagnosticRenderMode.Normal;
        string versionedKey = version + ":" + renderMode + ":" + typeKey;
        Type resolvedType = type;
        bool isStatic = obj is Type;
        bool drillDown = renderMode == DiagnosticRenderMode.DrillDown;
        return _typeHash.GetOrAdd(versionedKey, _ => BuildPropertyGetters(resolvedType, isStatic, drillDown));
    }

    /// <summary>
    ///     Builds the getters for one property, choosing the presentation strategy from the
    ///     attribute, the configuration, or the property's own type.
    /// </summary>
    /// <remarks>
    ///     The single seam between the configuration model and the getter pipeline. Both routes —
    ///     an attribute on the property, or a <see cref="PropertyConfiguration" /> built fluently —
    ///     arrive here and converge on the same getters, which is what stops the two ways of
    ///     configuring a property drifting apart.
    /// </remarks>
    internal static void AddPropertyGetters(
        ICollection<PropertyGetter> getters,
        PropertyInfo info,
        DiagnosticPropertyAttribute metadata,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes,
        string defaultFormat,
        bool useDefaultPropertyPresentation = false
    )
    {
        PropertyStrategy strategy = GetPropertyStrategy(info, metadata, configuration, useDefaultPropertyPresentation);
        switch (strategy)
        {
            case PropertyStrategy.Collection:
                AddCollectionGetters(getters, info, metadata, configuration, isStatic, applyAttributes, defaultFormat);
                break;
            case PropertyStrategy.Extended:
                getters.Add(
                    new ExtendedPropertyGetter(
                        info,
                        new ExtendedPropertyAttribute(),
                        metadata,
                        configuration,
                        isStatic,
                        applyAttributes,
                        defaultFormat
                    )
                );
                break;
            case PropertyStrategy.Rate:
                getters.Add(
                    new RateGetter(
                        info,
                        CreateRateOptions(metadata as RatePropertyAttribute, configuration),
                        metadata,
                        configuration,
                        isStatic,
                        applyAttributes,
                        defaultFormat
                    )
                );
                break;
            case PropertyStrategy.Date:
                getters.Add(
                    new DateGetter(
                        info,
                        CreateDateOptions(metadata as DatePropertyAttribute, configuration),
                        metadata,
                        configuration,
                        isStatic,
                        applyAttributes,
                        defaultFormat
                    )
                );
                break;
            default:
                getters.Add(
                    new PropertyGetter(
                        info,
                        metadata,
                        configuration,
                        isStatic,
                        applyAttributes,
                        defaultFormat,
                        useDefaultPropertyPresentation
                            && IsDefaultObjectType(info.PropertyType)
                            && !HasUsefulToString(info.PropertyType)
                    )
                );
                break;
        }
    }

    private static PropertyStrategy GetPropertyStrategy(
        PropertyInfo info,
        DiagnosticPropertyAttribute attribute,
        PropertyConfiguration configuration,
        bool useDefaultPropertyPresentation
    )
    {
        // An explicit configuration wins over everything; then an explicit attribute; then the
        // property's own type. Order matters: a RateCounter-typed property is a rate whether or not
        // it carries the attribute, but a configuration saying otherwise still overrides that.
        if (configuration?.Strategy != null)
        {
            return configuration.Strategy.Value;
        }

        if (attribute is CollectionPropertyAttribute)
        {
            return PropertyStrategy.Collection;
        }

        if (attribute is ExtendedPropertyAttribute)
        {
            return PropertyStrategy.Extended;
        }

        Type propertyType = info?.PropertyType ?? configuration?.ValueType;
        if (attribute is RatePropertyAttribute || propertyType == typeof(RateCounter))
        {
            return PropertyStrategy.Rate;
        }

        if (propertyType == null)
        {
            // A configured property can have neither a PropertyInfo nor a declared value type.
            // GetUnderlyingType guards against null by throwing, so fall back rather than fail.
            return PropertyStrategy.Default;
        }

        Type underlying = GetUnderlyingType(propertyType);
        if (
            attribute is DatePropertyAttribute
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
        )
        {
            return PropertyStrategy.Date;
        }

        if (configuration?.UsesPropertyDefaults == true && UsesDefaultCollectionPresentation(underlying, configuration))
        {
            return PropertyStrategy.Collection;
        }

        return useDefaultPropertyPresentation && IsDefaultCollectionType(underlying)
            ? PropertyStrategy.Collection
            : PropertyStrategy.Default;
    }

    private static bool UsesDefaultCollectionPresentation(Type type, PropertyConfiguration configuration)
    {
        return configuration.ValueFormatter == null
            && !configuration.FormatString.IsSet
            && IsConfiguredCollectionType(type);
    }

    private static bool IsConfiguredCollectionType(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        if (underlyingType == typeof(string))
        {
            return false;
        }

        if (underlyingType.IsArray)
        {
            return true;
        }

        return ImplementsGenericInterface(underlyingType, typeof(ICollection<>))
            || ImplementsGenericInterface(underlyingType, typeof(IList<>))
            || ImplementsGenericInterface(underlyingType, typeof(IReadOnlyCollection<>))
            || ImplementsGenericInterface(underlyingType, typeof(IReadOnlyList<>))
            || ImplementsGenericInterface(underlyingType, typeof(ISet<>));
    }

    private static bool ImplementsGenericInterface(Type type, Type genericInterface)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == genericInterface
            || type.GetInterfaces()
                .Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == genericInterface);
    }

    private static void AddCollectionGetters(
        ICollection<PropertyGetter> getters,
        PropertyInfo info,
        DiagnosticPropertyAttribute metadata,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes,
        string defaultFormat
    )
    {
        CollectionOptions source = (metadata as CollectionPropertyAttribute)?.CreateOptions();
        IReadOnlyList<CollectionOutputConfiguration> outputs = configuration?.CollectionOutputs;
        if (outputs == null || outputs.Count == 0)
        {
            CollectionOptions options = CloneCollectionOptions(source);
            ApplyCollectionConfiguration(options, configuration);
            getters.Add(
                new CollectionGetter(info, options, metadata, configuration, isStatic, applyAttributes, defaultFormat)
            );
            return;
        }

        // One collection property can be projected several ways at once - a count alongside a
        // listing, say - so each configured output becomes its own getter over the same property.
        foreach (CollectionOutputConfiguration output in outputs)
        {
            CollectionOptions options = CloneCollectionOptions(source);
            options.Mode = output.Mode;
            options.NameProperty = output.NameProperty ?? options.NameProperty;
            options.NameFormatter = output.NameFormatter ?? options.NameFormatter;
            options.IndexedNameFormatter = output.IndexedNameFormatter ?? options.IndexedNameFormatter;
            options.ValueProperty = output.ValueProperty ?? options.ValueProperty;
            options.ValueFormatter = output.ValueFormatter ?? options.ValueFormatter;
            options.ItemIsJson = output.ItemIsJson;
            options.DescriptionProperty = output.DescriptionProperty ?? options.DescriptionProperty;
            options.DescriptionFormatter = output.DescriptionFormatter ?? options.DescriptionFormatter;
            options.CategoryProperty = output.CategoryProperty ?? options.CategoryProperty;
            options.CategoryFormatter = output.CategoryFormatter ?? options.CategoryFormatter;
            options.Separator = output.Separator ?? options.Separator;
            options.InitiallyExpanded = output.InitiallyExpanded;
            options.PrimaryPropertiesOnly = output.PrimaryPropertiesOnly;
            options.ItemStatuses = output.ItemStatuses;
            options.ItemStatusIconSize = output.ItemStatusIconSize;
            options.ItemWidth = output.ItemWidth;
            ApplyCollectionConfiguration(options, configuration);

            PropertyConfiguration outputConfiguration = configuration.Clone();
            ApplyCollectionOutputConfiguration(outputConfiguration, output);
            string outputName = output.Name;
            if (outputName == null && outputs.Count > 1 && output.Mode == CollectionMode.Count)
            {
                // Several outputs over one property would otherwise collide on the same name.
                string baseName = configuration.Name.IsSet ? configuration.Name.Value : metadata?.Name ?? info?.Name;
                outputName = baseName + " count";
            }

            if (outputName != null)
            {
                // Set the name on the configuration that already carries this output's overrides.
                // Upstream re-clones from the original here, which silently discards everything
                // ApplyCollectionOutputConfiguration just applied - drill-down, hover, truncation -
                // for every named output.
                outputConfiguration.Name = new ConfiguredValue<string>(outputName);
            }

            getters.Add(
                new CollectionGetter(
                    info,
                    options,
                    metadata,
                    outputConfiguration,
                    isStatic,
                    applyAttributes,
                    defaultFormat
                )
            );
        }
    }

    private static CollectionOptions CloneCollectionOptions(CollectionOptions source)
    {
        if (source == null)
        {
            return new CollectionOptions(CollectionMode.Count);
        }

        return new CollectionOptions(source.Mode)
        {
            NameProperty = source.NameProperty,
            NameFormatter = source.NameFormatter,
            IndexedNameFormatter = source.IndexedNameFormatter,
            ValueProperty = source.ValueProperty,
            ValueFormatter = source.ValueFormatter,
            ItemIsJson = source.ItemIsJson,
            DescriptionProperty = source.DescriptionProperty,
            DescriptionFormatter = source.DescriptionFormatter,
            CategoryProperty = source.CategoryProperty,
            CategoryFormatter = source.CategoryFormatter,
            Separator = source.Separator,
            MaxItems = source.MaxItems,
            InitiallyExpanded = source.InitiallyExpanded,
            PrimaryPropertiesOnly = source.PrimaryPropertiesOnly,
            ItemStatuses = source.ItemStatuses,
            ItemStatusIconSize = source.ItemStatusIconSize,
            ItemWidth = source.ItemWidth,
        };
    }

    private static void ApplyCollectionConfiguration(CollectionOptions options, PropertyConfiguration configuration)
    {
        if (configuration != null && configuration.MaxItems.IsSet)
        {
            options.MaxItems = configuration.MaxItems.Value;
        }
    }

    private static void ApplyCollectionOutputConfiguration(
        PropertyConfiguration configuration,
        CollectionOutputConfiguration output
    )
    {
        configuration.NoTruncate = output.NoTruncate.Or(configuration.NoTruncate);
        configuration.DrillDown = output.DrillDown.Or(configuration.DrillDown);
        configuration.DrillDownMaxItems = output.DrillDownMaxItems.Or(configuration.DrillDownMaxItems);
        configuration.DrillDownIconOnly = output.DrillDownIconOnly.Or(configuration.DrillDownIconOnly);
        configuration.DrillDownText = output.DrillDownText.Or(configuration.DrillDownText);
        configuration.DrillDownTextFormatter = output.DrillDownTextFormatter ?? configuration.DrillDownTextFormatter;
        configuration.JsonHover = output.JsonHover.Or(configuration.JsonHover);
        configuration.ExpandedHover = output.ExpandedHover.Or(configuration.ExpandedHover);
    }

    private static RatePropertyAttribute CreateRateOptions(
        RatePropertyAttribute source,
        PropertyConfiguration configuration
    )
    {
        if (source == null && configuration?.Strategy != PropertyStrategy.Rate)
        {
            return null;
        }

        RatePropertyAttribute options =
            source == null
                ? new RatePropertyAttribute()
                : new RatePropertyAttribute { ExposeRate = source.ExposeRate, ExposeTotal = source.ExposeTotal };

        if (configuration != null)
        {
            if (configuration.ExposeRate.IsSet)
            {
                options.ExposeRate = configuration.ExposeRate.Value;
            }

            if (configuration.ExposeTotal.IsSet)
            {
                options.ExposeTotal = configuration.ExposeTotal.Value;
            }
        }

        return options;
    }

    /// <summary>
    ///     Builds date options, or null when neither an attribute nor the configuration says
    ///     anything about dates.
    /// </summary>
    /// <remarks>
    ///     Returning null matters. DateGetter defaults ExposeDate to true and overrides it only when
    ///     handed a non-null attribute, while our DatePropertyAttribute.ExposeDate defaults to false
    ///     - upstream's carries a constructor setting it true, ours does not. Handing back a default
    ///     attribute for an unattributed DateTime property would therefore switch the date off for
    ///     every one of them, silently. Same guard shape as CreateRateOptions.
    /// </remarks>
    private static DatePropertyAttribute CreateDateOptions(
        DatePropertyAttribute source,
        PropertyConfiguration configuration
    )
    {
        bool configuresDates =
            configuration != null
            && (
                configuration.ExposeDate.IsSet
                || configuration.ExposeElapsed.IsSet
                || configuration.ExposeTimeUntil.IsSet
            );
        if (source == null && !configuresDates)
        {
            return null;
        }

        DatePropertyAttribute options =
            source == null
                ? new DatePropertyAttribute { ExposeDate = true }
                : new DatePropertyAttribute
                {
                    ExposeDate = source.ExposeDate,
                    ExposeElapsed = source.ExposeElapsed,
                    ExposeTimeUntil = source.ExposeTimeUntil,
                    IsUTC = source.IsUTC,
                };

        if (configuration != null)
        {
            if (configuration.ExposeDate.IsSet)
            {
                options.ExposeDate = configuration.ExposeDate.Value;
            }

            if (configuration.ExposeElapsed.IsSet)
            {
                options.ExposeElapsed = configuration.ExposeElapsed.Value;
            }

            if (configuration.ExposeTimeUntil.IsSet)
            {
                options.ExposeTimeUntil = configuration.ExposeTimeUntil.Value;
            }
        }

        return options;
    }

    private static bool IsDefaultObjectType(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        return !IsDefaultDiagnosticPropertyType(underlyingType)
            && !IsDefaultCollectionType(underlyingType)
            && !IsExcludedDefaultDiagnosticPropertyType(underlyingType);
    }

    private static bool IsDefaultDiagnosticPropertyType(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        return underlyingType == typeof(string)
            || underlyingType.IsPrimitive
            || underlyingType.IsEnum
            || underlyingType == typeof(decimal)
            || underlyingType == typeof(DateTime)
            || underlyingType == typeof(DateTimeOffset)
            || underlyingType == typeof(TimeSpan)
            || underlyingType == typeof(Guid);
    }

    /// <summary>
    ///     Tasks are excluded from default object presentation: walking one's properties forces
    ///     evaluation of Result and can deadlock or surface an exception that was never observed.
    /// </summary>
    private static bool IsExcludedDefaultDiagnosticPropertyType(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        return underlyingType.Namespace?.StartsWith("System.Threading.Tasks", StringComparison.Ordinal) == true;
    }

    private static bool IsDefaultCollectionType(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        return underlyingType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(underlyingType);
    }

    /// <summary>
    ///     Whether a type overrides ToString itself. A type that does not would render as its type
    ///     name, which is worth nothing, so the default presentation expands it instead.
    /// </summary>
    private static bool HasUsefulToString(Type type)
    {
        Type underlyingType = GetUnderlyingType(type);
        MethodInfo toString = underlyingType.GetMethod(nameof(ToString), Type.EmptyTypes);
        return toString != null
            && toString.DeclaringType != typeof(object)
            && toString.DeclaringType != typeof(ValueType);
    }

    /// <summary>
    ///     Builds the getters for one type, from its attributes and from whatever the configuration
    ///     in force says about it.
    /// </summary>
    /// <remarks>
    ///     The strategy dispatch that used to live here now sits in <see cref="AddPropertyGetters" />,
    ///     so an attribute-configured and a fluently-configured property go through one path. This
    ///     is where the configuration stops being inert.
    /// </remarks>
    private static List<PropertyGetter> BuildPropertyGetters(Type type, bool isStatic, bool drillDown = false)
    {
        List<PropertyGetter> propertyList = [];
        DiagnosticConfigurationSnapshot configuration = _configuration;
        bool applyAttributes = configuration.ApplyAttributes;
        TypeConfiguration typeConfiguration = isStatic
            ? null
            : configuration.GetEffectiveTypeConfiguration(type, drillDown);

        IEnumerable<PropertyInfo> properties = isStatic
            ? GetStaticProperties(type)
            : GetInstanceProperties(type, null, typeConfiguration);
        foreach (PropertyInfo info in properties)
        {
            DiagnosticPropertyAttribute propAttr = applyAttributes
                ? GetAttribute<DiagnosticPropertyAttribute>(info)
                : null;
            PropertyConfiguration propertyConfiguration = typeConfiguration?.Find(info);
            string defaultFormat = configuration.GetDefaultFormat(info.PropertyType);
            AddPropertyGetters(
                propertyList,
                info,
                propAttr,
                propertyConfiguration,
                isStatic,
                applyAttributes,
                defaultFormat
            );
        }

        if (typeConfiguration != null)
        {
            // Properties that exist only in configuration: a delegate over the object, or a fully
            // custom projection. Neither has a PropertyInfo, so neither is reachable above.
            foreach (PropertyConfiguration delegateProperty in typeConfiguration.DelegateProperties)
            {
                string defaultFormat = configuration.GetDefaultFormat(delegateProperty.ValueType);
                AddPropertyGetters(propertyList, null, null, delegateProperty, false, false, defaultFormat);
            }

            foreach (CustomPropertyConfiguration customProperty in typeConfiguration.CustomProperties)
            {
                propertyList.Add(new CustomPropertyGetter(customProperty));
            }
        }

        return propertyList;
    }

    private static IEnumerable<PropertyInfo> GetInstanceProperties(
        Type type,
        DiagnosticClassAttribute inheritedAttr,
        TypeConfiguration typeConfiguration
    )
    {
        return GetInstanceProperties(
            type,
            inheritedAttr,
            typeConfiguration,
            new HashSet<string>(StringComparer.Ordinal)
        );
    }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "This recursive reflection walk preserves inherited diagnostic metadata."
    )]
    [SuppressMessage(
        "CodeQuality",
        "S3267:Loops should be simplified with LINQ expressions",
        Justification = "The loop yields recursively discovered properties and cannot be flattened safely."
    )]
    private static IEnumerable<PropertyInfo> GetInstanceProperties(
        Type type,
        DiagnosticClassAttribute inheritedAttr,
        TypeConfiguration typeConfiguration,
        HashSet<string> yieldedNames
    )
    {
        if (type != typeof(object) && type != null)
        {
            DiagnosticClassAttribute diagAttr = GetAttribute<DiagnosticClassAttribute>(type, false);

            if (inheritedAttr == null)
            {
                for (
                    Type baseType = type.BaseType;
                    baseType != null && baseType != typeof(object);
                    baseType = baseType.BaseType
                )
                {
                    DiagnosticClassAttribute attr = GetAttribute<DiagnosticClassAttribute>(baseType, false);
                    if (attr != null)
                    {
                        if (!attr.DeclaringTypeOnly)
                        {
                            inheritedAttr = attr;
                        }
                        break;
                    }
                }
            }

            if (inheritedAttr == null || !inheritedAttr.DeclaringTypeOnly || diagAttr != null)
            {
                foreach (
                    PropertyInfo propInfo in type.GetProperties(PublicInstancePropertyFlags | BindingFlags.DeclaredOnly)
                        .Where(p => ShouldIncludeProperty(diagAttr ?? inheritedAttr, p, typeConfiguration))
                )
                {
                    if (yieldedNames.Add(propInfo.Name))
                    {
                        yield return propInfo;
                    }
                }
            }

            foreach (
                PropertyInfo propInfo in GetInstanceProperties(
                    type.BaseType,
                    diagAttr ?? inheritedAttr,
                    typeConfiguration,
                    yieldedNames
                )
            )
            {
                yield return propInfo;
            }
        }
    }

    private static IEnumerable<PropertyInfo> GetStaticProperties(Type type)
    {
        DiagnosticClassAttribute diagAttr = GetAttribute<DiagnosticClassAttribute>(type, false);

        return type.GetProperties(PublicStaticPropertyFlags)
            .Where(propInfo => ShouldIncludeProperty(diagAttr, propInfo, null));
    }

    private static bool ShouldIncludeProperty(
        DiagnosticClassAttribute diagAttr,
        PropertyInfo info,
        TypeConfiguration typeConfiguration
    )
    {
        if (info.PropertyType == typeof(EventSink))
        {
            return false;
        }

        // An explicit Include or Exclude for this property outranks everything else, including
        // an attribute, because it is the most specific thing anyone said about it.
        if (typeConfiguration?.Find(info)?.Included is bool included)
        {
            return included;
        }

        bool attributedOnly = diagAttr is { AttributedPropertiesOnly: true };
        BrowsableAttribute browseAttr = GetAttribute<BrowsableAttribute>(info);
        DiagnosticPropertyAttribute propAttr = GetAttribute<DiagnosticPropertyAttribute>(info);

        if (propAttr != null)
        {
            return !propAttr.Ignore;
        }

        if (browseAttr is { Browsable: false })
        {
            return false;
        }

        // IncludeAll / ExcludeAll, which apply to everything this type did not speak about
        // individually. Placed after the attribute checks so an explicit [Browsable(false)] or
        // [DiagnosticProperty(Ignore = true)] still wins.
        if (typeConfiguration?.IncludeAll is bool includeAll)
        {
            return includeAll;
        }

        if (attributedOnly)
        {
            return browseAttr != null;
        }

        return true;
    }

    public static Type GetUnderlyingType(Type t)
    {
        Guard.NotNull(t, nameof(t));

        if (!t.IsGenericType)
        {
            return t;
        }

        if (t.GetGenericTypeDefinition() != typeof(Nullable<>))
        {
            return t;
        }

        return t.GetGenericArguments()[0];
    }

    private static T GetAttribute<T>(PropertyInfo info)
        where T : Attribute
    {
        object[] attrs = info.GetCustomAttributes(typeof(T), false);
        if (attrs.Length == 0)
        {
            return null;
        }

        return attrs[0] as T;
    }

    private static T GetAttribute<T>(Type info, bool inherit)
        where T : Attribute
    {
        object[] attrs = info.GetCustomAttributes(typeof(T), inherit);
        if (attrs.Length == 0)
        {
            return null;
        }

        return attrs[0] as T;
    }

    public static OperationResponse ExecuteOperation(string path, string operation, string[] arguments)
    {
        return ExecuteOperation(GetRegisteredObjects(), path, operation, arguments);
    }

    /// <summary>
    ///     Runs an operation against the drilldown named by <paramref name="objectPaths" />.
    /// </summary>
    /// <remarks>
    ///     An empty chain is the main view and behaves exactly as the overload without one. A chain
    ///     means the operator triggered this inside a drilldown, where <paramref name="path" /> is
    ///     relative to that popup's own diagnostics.
    /// </remarks>
    public static OperationResponse ExecuteOperation(
        string[] objectPaths,
        string path,
        string operation,
        string[] arguments
    )
    {
        return ExecuteOperation(GetRegisteredObjects(), objectPaths, path, operation, arguments);
    }

    /// <inheritdoc cref="ExecuteOperation(string[], string, string, string[])" />
    public static OperationResponse ExecuteOperation(
        IEnumerable<RegisteredObject> registeredObjects,
        string[] objectPaths,
        string path,
        string operation,
        string[] arguments
    )
    {
        try
        {
            IEnumerable<RegisteredObject> targets = ResolveActionObjects(registeredObjects, objectPaths);
            return InActionRenderMode(objectPaths, () => ExecuteOperation(targets, path, operation, arguments));
        }
        catch (Exception ex)
        {
            return OperationResponse.Error(ex.Message, ex.ToString());
        }
    }

    public static OperationResponse ExecuteOperation(
        IEnumerable<RegisteredObject> registeredObjects,
        string path,
        string operation,
        string[] arguments
    )
    {
        if (path == null)
        {
            return OperationResponse.Error("Object path not specified");
        }

        try
        {
            if (arguments == null)
            {
                arguments = [];
            }

            PropIdent ident = PropIdent.Parse(path);
            object sourceObject = GetSourceObject(registeredObjects, ident);
            OperationSet opSet = GetOperationSet(sourceObject);
            if (opSet == null)
            {
                throw new ArgumentException($"Can't find operations for {ident}");
            }

            Operation op = opSet.Operations.FirstOrDefault(x => x.Signature == operation);
            if (op == null)
            {
                throw new ArgumentException($"Operation '{operation}' not found");
            }

            ParameterInfo[] parameters = op.MethodInfo.GetParameters();

            if (parameters.Length != arguments.Length)
            {
                string msg =
                    $"Operation {operation} expected {parameters.Length} parameters, only found {arguments.Length}";
                throw new ArgumentException(msg);
            }
            object[] paramVals = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                try
                {
                    paramVals[i] = ConvertValue(parameters[i].ParameterType, arguments[i]);
                }
                catch (Exception ex)
                {
                    string msg =
                        $"Parameter {i + 1} ({parameters[i].Name}) can't convert '{arguments[i]}' to {TypeUtil.GetFriendlyTypeName(parameters[i].ParameterType)}";
                    throw new ArgumentException(msg, ex);
                }
            }

            object result = op.MethodInfo.Invoke(sourceObject, paramVals);
            string resultString = OperationResultToString(result);
            return OperationResponse.Success(resultString);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            // MethodInfo.Invoke wraps anything the operation body throws in a
            // TargetInvocationException whose Message is the useless boilerplate
            // "Exception has been thrown by the target of an invocation." Surface the
            // real exception instead — for a diagnostics tool the whole point of running
            // an operation is to learn why it failed.
            Exception inner = ex.InnerException;
            return OperationResponse.Error(inner.Message, inner.ToString());
        }
        catch (Exception ex)
        {
            return OperationResponse.Error(ex.Message, ex.ToString());
        }
    }

    private static string OperationResultToString(object obj)
    {
        if (obj == null)
        {
            return null;
        }

        if (obj is string str)
        {
            return str;
        }

        if (obj is not IEnumerable asEnumerable)
        {
            return Convert.ToString(obj);
        }

        string[] values = [.. asEnumerable.Cast<object>().Select(Convert.ToString)];
        if (values.Length == 0)
        {
            return "<Empty>";
        }

        return "[" + string.Join(", ", values) + "]";
    }

    /// <summary>
    /// Given an identifer which specifies the path required, this method finds the object which
    /// represents the given PropertyBag/Property Category/Property
    /// </summary>
    /// <param name="registeredObjects">The objects to search within</param>
    /// <param name="ident">Identifies the BagCat/BagName/PropCat/PropName we are searching for</param>
    /// <returns>An object which represents the Bag/PropCat/Prop, or exception if not found</returns>
    /// <summary>
    ///     Resolves an operation's path to the object it names.
    /// </summary>
    /// <remarks>
    ///     Reads the ambient render mode rather than assuming the main view, for the same reason
    ///     <see cref="GetPropertyGetters" /> does: an operation triggered inside a drilldown names
    ///     its target in the drilldown's own diagnostics, and a category or property that only the
    ///     drilldown configuration places there does not exist in a main-view render. Assuming
    ///     Normal here would re-render the drilled object under the wrong configuration and fail
    ///     the lookup for an operation the UI had just offered.
    /// </remarks>
    private static object GetSourceObject(IEnumerable<RegisteredObject> registeredObjects, PropIdent ident)
    {
        DiagnosticRenderMode renderMode = _renderMode.Value ?? DiagnosticRenderMode.Normal;
        return GetSourceTarget(registeredObjects, ident, renderMode, DrillDownAccess.None).Value;
    }

    /// <summary>
    ///     Resolves one path to the value it names, and to how many of that value's items may be
    ///     shown.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="access" /> decides which value a property yields and whether it may
    ///         be yielded at all. An operation or a property edit reads
    ///         <see cref="Property.ValueObject" /> and is not gated here — the operation set is its
    ///         own check. A drilldown reads <see cref="Property.DrillDownObject" />, which the
    ///         getter pipeline sets on any inspectable value regardless of configuration, so the
    ///         gate is applied HERE rather than left to the browser.
    ///     </para>
    ///     <para>
    ///         That gate is the difference between "drilldowns are opt-in", which is what the
    ///         configuration says and the UI honours, and it actually being true: without it a
    ///         request naming a path the UI never offered opens any object-valued property on the
    ///         process, and a JSON hover returns that object's whole graph as text.
    ///     </para>
    /// </remarks>
    private static DrillDownTarget GetSourceTarget(
        IEnumerable<RegisteredObject> registeredObjects,
        PropIdent ident,
        DiagnosticRenderMode renderMode,
        DrillDownAccess access
    )
    {
        PropertyBag bag = GetRegisteredObject(registeredObjects, ident, renderMode);
        if (string.IsNullOrEmpty(ident.PropCategory) && string.IsNullOrEmpty(ident.PropName))
        {
            return GetBagTarget(bag, ident, access, renderMode);
        }
        Category cat = bag.Categories.FindByName(ident.PropCategory);
        if (cat == null)
        {
            throw new ArgumentException(
                $"Can't find source category {ident.BagCategory}|{ident.BagName}|{ident.PropCategory}"
            );
        }
        return string.IsNullOrEmpty(ident.PropName)
            ? GetCategoryTarget(cat, ident, access)
            : GetPropertyTarget(cat, ident, access);
    }

    /// <summary>
    ///     Resolves a hop that names a whole bag rather than a property inside one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The host's opt-in is checked on the OUTERMOST hop only, and that is the whole of the
    ///         gate: it is what stops a request naming a bag the UI never offered.
    ///     </para>
    ///     <para>
    ///         An inner hop is a different question. The bags a chain resolves against after the
    ///         first are the ones the previous hop just produced - the items of a collection the
    ///         operator already has open, each rendered in drilldown mode and so reporting
    ///         <see cref="PropertyBag.CanDrillDown" /> false because it IS the thing being looked
    ///         at. Requiring the flag there would refuse every action on a list item and every
    ///         drilldown from a list into one of its rows, which is the ordinary interaction, while
    ///         granting no access the already-gated outer hop had not granted.
    ///     </para>
    /// </remarks>
    private static DrillDownTarget GetBagTarget(
        PropertyBag bag,
        PropIdent ident,
        DrillDownAccess access,
        DiagnosticRenderMode renderMode
    )
    {
        if (bag.SourceObject == null)
        {
            string msg =
                $"Can't invoke operation. Property bag {ident.BagCategory}|{ident.BagName} doesn't have a value.";
            throw new ArgumentException(msg);
        }
        bool outermost = renderMode == DiagnosticRenderMode.Normal;
        if (access != DrillDownAccess.None && outermost && !bag.CanDrillDown)
        {
            throw new ArgumentException($"{ident.BagCategory}|{ident.BagName} is not available for drilldown.");
        }

        // Bag-level drilldown has no JSON-specific opt-in (unlike WithJsonHover() on a property),
        // so CanDrillDown alone must never be read as JSON permission: that would serialise the
        // bag's whole public object graph -- past [Browsable(false)]/Ignore/ExcludeAll -- to any
        // caller who can reach ordinary drilldown. Fail closed until bag-level JSON opt-in exists.
        if (access == DrillDownAccess.Json)
        {
            throw new ArgumentException($"{ident.BagCategory}|{ident.BagName} is not available for JSON view.");
        }
        return new DrillDownTarget(bag.SourceObject, DrillDownMaxItems);
    }

    private static DrillDownTarget GetCategoryTarget(Category cat, PropIdent ident, DrillDownAccess access)
    {
        if (cat.ValueObject == null)
        {
            string msg =
                $"Can't invoke operation. Category {ident.BagCategory}|{ident.BagName}|{ident.PropCategory} doesn't have a value.";
            throw new ArgumentException(msg);
        }
        if (access == DrillDownAccess.None)
        {
            return new DrillDownTarget(cat.ValueObject, cat.DrillDownMaxItems);
        }
        if (!cat.CanDrillDown || cat.DrillDownObject == null)
        {
            throw new ArgumentException($"Category {ident} is not available for drilldown.");
        }

        // Same fail-closed reasoning as GetBagTarget: Category has no JSON-specific opt-in, so
        // ordinary drilldown permission must not also grant raw JSON serialization of the value.
        if (access == DrillDownAccess.Json)
        {
            throw new ArgumentException($"Category {ident} is not available for JSON view.");
        }
        return new DrillDownTarget(cat.DrillDownObject, cat.DrillDownMaxItems);
    }

    private static DrillDownTarget GetPropertyTarget(Category cat, PropIdent ident, DrillDownAccess access)
    {
        Property prop = cat.Properties.FindByName(ident.PropName);
        if (prop == null)
        {
            string msg =
                $"Can't invoke operation. Property {ident.BagCategory}|{ident.BagName}|{ident.PropCategory} not found.";
            throw new ArgumentException(msg);
        }
        if (access == DrillDownAccess.None)
        {
            if (prop.ValueObject == null)
            {
                string msg =
                    $"Can't invoke operation. Property {ident.BagCategory}|{ident.BagName}|{ident.PropCategory} doesn't have a value.";
                throw new ArgumentException(msg);
            }
            return new DrillDownTarget(prop.ValueObject, prop.DrillDownMaxItems, ident.PropName);
        }
        bool permitted =
            access == DrillDownAccess.Json ? prop.CanJsonHover : prop.CanDrillDown || prop.CanExpandedHover;
        if (!permitted || prop.DrillDownObject == null)
        {
            throw new ArgumentException($"Property {ident} is not available for drilldown.");
        }
        return new DrillDownTarget(prop.DrillDownObject, prop.DrillDownMaxItems, ident.PropName);
    }

    /// <summary>What a path resolution is for, which decides both the value read and the gate.</summary>
    private enum DrillDownAccess
    {
        /// <summary>An operation or property edit: the rendered value, ungated.</summary>
        None,

        /// <summary>A drilldown or expanded hover: the inspectable value, if the host allowed it.</summary>
        Inspect,

        /// <summary>A JSON hover: the same value, gated on the host having allowed JSON hover.</summary>
        Json,
    }

    #region SetProperty

    public static OperationResponse SetProperty(string path, string value)
    {
        return SetProperty(GetRegisteredObjects(), path, value);
    }

    /// <summary>Sets a property inside the drilldown named by <paramref name="objectPaths" />.</summary>
    public static OperationResponse SetProperty(string[] objectPaths, string path, string value)
    {
        return SetProperty(GetRegisteredObjects(), objectPaths, path, value);
    }

    /// <inheritdoc cref="SetProperty(string[], string, string)" />
    public static OperationResponse SetProperty(
        IEnumerable<RegisteredObject> registeredObjects,
        string[] objectPaths,
        string path,
        string value
    )
    {
        try
        {
            IEnumerable<RegisteredObject> targets = ResolveActionObjects(registeredObjects, objectPaths);
            return InActionRenderMode(objectPaths, () => SetProperty(targets, path, value));
        }
        catch (Exception ex)
        {
            return OperationResponse.Error(ex.Message, ex.ToString());
        }
    }

    public static OperationResponse SetProperty(
        IEnumerable<RegisteredObject> registeredObjects,
        string path,
        string value
    )
    {
        try
        {
            if (path == null)
            {
                return OperationResponse.Error("Property path not specified");
            }

            PropIdent ident = PropIdent.Parse(path);
            RegisteredObject regObj = registeredObjects.FindByCategoryAndName(ident.BagCategory, ident.BagName);
            if (regObj == null)
            {
                return OperationResponse.Error($"Can't find PropertyBag {ident.BagCategory}.{ident.BagName}");
            }

            object obj = regObj.Object;
            if (obj == null)
            {
                return OperationResponse.Error(
                    $"PropertyBag {ident.BagCategory}.{ident.BagName} was garbage collected just before I could set the property.  How bizarre!"
                );
            }

            List<PropertyGetter> valueGetters = GetPropertyGetters(obj);
            PropertyGetter getter = valueGetters.FirstOrDefault(g =>
                _ignoreCase.Equals(g.Name, ident.PropName)
                && _ignoreCase.Equals(g.Category ?? "", ident.PropCategory ?? "")
            );

            if (getter == null)
            {
                return OperationResponse.Error($"Can't find property [{ident.PropCategory}].[{ident.PropName}]");
            }

            if (!getter.CanSet)
            {
                return OperationResponse.Error(
                    $"You are not allowed to set [{ident.PropCategory}].[{ident.PropName}], AllowSet is not enabled!"
                );
            }

            if (getter.PropInfo == null)
            {
                return OperationResponse.Error(
                    $"'{ident.PropCategory}'.'{ident.PropName}' doesn't have a source PropertyInfo!"
                );
            }

            bool isType = obj is Type;
            Type declaringType = getter.PropInfo.DeclaringType;
            if (!isType && (declaringType == null || !declaringType.IsInstanceOfType(obj)))
            {
                return OperationResponse.Error(
                    $"'{ident.PropCategory}'.'{ident.PropName}' property {getter.PropInfo.Name} expects type {declaringType?.Name ?? "<unknown>"}, got {obj.GetType().Name}"
                );
            }

            object newValue = ConvertValue(getter.PropInfo.PropertyType, value);
            getter.PropInfo.SetValue(isType ? null : obj, newValue, null);

            return OperationResponse.Success();
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            Exception inner = ex.InnerException;
            return OperationResponse.Error(inner.Message, inner.ToString());
        }
        catch (Exception ex)
        {
            return OperationResponse.Error(ex.Message, ex.ToString());
        }
    }

    private static PropertyBag GetRegisteredObject(
        IEnumerable<RegisteredObject> registeredObjects,
        PropIdent ident,
        DiagnosticRenderMode renderMode = DiagnosticRenderMode.Normal
    )
    {
        RegisteredObject regObj = registeredObjects.FindByCategoryAndName(ident.BagCategory, ident.BagName);
        if (regObj == null)
        {
            throw new ArgumentException($"Can't find PropertyBag {ident.BagCategory}.{ident.BagName}");
        }

        object obj = regObj.Object;
        if (obj == null)
        {
            string msg =
                $"PropertyBag {ident.BagCategory}.{ident.BagName} was garbage collected just before I could set the property.  How bizarre!";
            throw new ArgumentException(msg);
        }

        return ObjectToPropertyBag(obj, ident.BagName, ident.BagCategory, renderMode);
    }

    #endregion

    #region DrillDown

    public static DrillDownResponse GetDrillDown(DrillDownRequest request)
    {
        return GetDrillDown(GetRegisteredObjects(), request);
    }

    /// <summary>
    ///     Renders the value a drilldown request names, as diagnostics or as JSON.
    /// </summary>
    /// <remarks>
    ///     Every failure comes back as an <see cref="DrillDownResponse.ErrorMessage" /> rather than
    ///     a fault. The caller is a client result on the agent's hub connection: a thrown exception
    ///     travels to the service as a failed invocation, and a request naming a path that no
    ///     longer resolves — an object replaced between poll and click — is an ordinary outcome,
    ///     not a fault of the connection.
    /// </remarks>
    public static DrillDownResponse GetDrillDown(
        IEnumerable<RegisteredObject> registeredObjects,
        DrillDownRequest request
    )
    {
        if (request == null)
        {
            return new DrillDownResponse { ErrorMessage = "Drilldown request not specified" };
        }

        try
        {
            DrillDownAccess access = request.JsonHover ? DrillDownAccess.Json : DrillDownAccess.Inspect;
            DrillDownTarget target = ResolveDrillDownTarget(registeredObjects, request.ObjectPaths, access);

            if (IsUserInterfaceElement(target.Value.GetType()))
            {
                return new DrillDownResponse
                {
                    ErrorMessage = "Windows Forms and WPF user interface elements cannot be shown in a drilldown.",
                };
            }

            if (request.JsonHover)
            {
                return SerializeJsonHover(target.Value);
            }

            DrillDownMaterialization materialized = MaterializeDrillDown(target);
            return new DrillDownResponse
            {
                Diagnostics = GetDiagnostics(materialized.Objects, DiagnosticRenderMode.DrillDown),
                DisplayedCount = materialized.DisplayedCount,
                TotalCount = materialized.TotalCount,
                IsTruncated = materialized.IsTruncated,
                EventViews = request.ExcludeEventViews ? [] : ResolveDrillDownEventViews(materialized.Objects),
            };
        }
        catch (Exception ex)
        {
            return new DrillDownResponse { ErrorMessage = ex.Message, ErrorDetail = ex.ToString() };
        }
    }

    private static readonly JsonSerializerOptions _jsonHoverOptions = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    /// <summary>
    ///     Refused rather than cut: half a JSON document is not JSON, and a client parsing it would
    ///     report a syntax error instead of the size. Serializes through a length-bounded stream
    ///     so an unbounded or lazily-enumerated graph (an <see cref="IEnumerable{T}"/> backed by a
    ///     query or generator) is stopped mid-write instead of first being fully materialized into a
    ///     string and only then measured -- the ceiling is meant to protect the host process being
    ///     diagnosed, not just the wire.
    /// </summary>
    private static DrillDownResponse SerializeJsonHover(object value)
    {
        using BoundedMemoryStream stream = new(MaxJsonHoverLength);
        try
        {
            using Utf8JsonWriter writer = new(stream);
            JsonSerializer.Serialize(writer, value, _jsonHoverOptions);
        }
        catch (JsonHoverLengthExceededException)
        {
            return new DrillDownResponse
            {
                ErrorMessage =
                    $"The value serialises to over the {MaxJsonHoverLength:N0} character limit for a JSON view.",
            };
        }

        string json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        return new DrillDownResponse { Json = json };
    }

    /// <summary>A <see cref="MemoryStream" /> that fails fast once more than <paramref name="maxLength" /> bytes are written, instead of buffering an unbounded write.</summary>
    private sealed class BoundedMemoryStream(int maxLength) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maxLength)
            {
                throw new JsonHoverLengthExceededException();
            }
            base.Write(buffer, offset, count);
        }
    }

    /// <summary>Internal-use signal, not a fault a caller of this assembly needs to catch.</summary>
    public sealed class JsonHoverLengthExceededException : Exception;

    /// <summary>
    ///     Collects the event tables the drilled-into objects define, merging by destination.
    /// </summary>
    /// <remarks>
    ///     Definitions only: matchers a client applies to events it already receives. Two objects
    ///     routing to the same destination produce one table admitting both, which is what lets a
    ///     collection drilldown show one table covering every item while drilling into one item
    ///     shows only its own.
    /// </remarks>
    private static List<DrillDownEventViewDefinition> ResolveDrillDownEventViews(
        IEnumerable<RegisteredObject> registeredObjects
    )
    {
        Dictionary<string, DrillDownEventViewDefinition> views = new(StringComparer.OrdinalIgnoreCase);
        // A registered object held weakly can have been collected since the caller took the list.
        foreach (object target in registeredObjects.Select(o => o?.Object).Where(o => o != null))
        {
            TypeConfiguration configuration = _configuration.GetEffectiveTypeConfiguration(
                target.GetType(),
                drillDown: true
            );
            foreach (DrillDownEventRouteTemplate route in configuration.EventRoutes)
            {
                AddEventView(views, route, target);
            }
        }
        return [.. views.Values.OrderBy(view => view.Category).ThenBy(view => view.Name)];
    }

    private static void AddEventView(
        Dictionary<string, DrillDownEventViewDefinition> views,
        DrillDownEventRouteTemplate route,
        object target
    )
    {
        string loggerName = route.ResolveLoggerName(target);
        if (string.IsNullOrWhiteSpace(loggerName))
        {
            return;
        }
        // The same separator the browser's destination key uses, and one no logger category or
        // destination name can contain.
        string id = $"{route.Route.Category}\u001F{route.Route.Name}";
        if (!views.TryGetValue(id, out DrillDownEventViewDefinition view))
        {
            view = new DrillDownEventViewDefinition
            {
                Id = id,
                Category = route.Route.Category,
                Name = route.Route.Name,
            };
            views.Add(id, view);
        }
        DrillDownEventMatcher matcher = new()
        {
            LoggerName = loggerName,
            LoggerNameMatchMode = route.MatchMode,
            MinLevel = route.Route.MinLevel.HasValue ? (int)route.Route.MinLevel.Value : null,
            MaxLevel = route.Route.MaxLevel.HasValue ? (int)route.Route.MaxLevel.Value : null,
        };
        // Two objects can resolve the same route to the same matcher - a static logger name shared
        // across a collection - and one table listing it twice would admit every event twice.
        if (!view.Matchers.Any(existing => IsSameMatcher(existing, matcher)))
        {
            view.Matchers.Add(matcher);
        }
    }

    private static bool IsSameMatcher(DrillDownEventMatcher left, DrillDownEventMatcher right)
    {
        return left.LoggerName == right.LoggerName
            && left.LoggerNameMatchMode == right.LoggerNameMatchMode
            && left.MinLevel == right.MinLevel
            && left.MaxLevel == right.MaxLevel;
    }

    /// <summary>
    ///     Walks a chain of paths, each resolved against the diagnostics the previous one produced.
    /// </summary>
    private static DrillDownTarget ResolveDrillDownTarget(
        IEnumerable<RegisteredObject> registeredObjects,
        IReadOnlyList<string> objectPaths,
        DrillDownAccess access
    )
    {
        if (objectPaths == null || objectPaths.Count == 0)
        {
            throw new ArgumentException("At least one drilldown object path is required.", nameof(objectPaths));
        }

        if (objectPaths.Count > MaxDrillDownDepth)
        {
            string msg =
                $"A drilldown chain of {objectPaths.Count} exceeds the limit of {MaxDrillDownDepth}, and each step costs a full render of the one before it.";
            throw new ArgumentException(msg, nameof(objectPaths));
        }

        IEnumerable<RegisteredObject> currentObjects = registeredObjects;
        DrillDownTarget current = null;
        for (int index = 0; index < objectPaths.Count; index++)
        {
            string path = objectPaths[index];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException($"Drilldown object path {index + 1} is empty.", nameof(objectPaths));
            }

            bool last = index + 1 == objectPaths.Count;
            try
            {
                current = GetSourceTarget(
                    currentObjects,
                    PropIdent.Parse(path),
                    // Only the outermost hop reads the process's own diagnostics. Every hop after
                    // it is reading a value already inside a drilldown, and so must be rendered
                    // with the drilldown configuration or the path it names will not exist.
                    index == 0
                        ? DiagnosticRenderMode.Normal
                        : DiagnosticRenderMode.DrillDown,
                    // A JSON view is permitted only for the value finally asked for. Nesting
                    // through it is an ordinary inspection and gated as one.
                    last ? access : DrillDownAccess.Inspect
                );
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Unable to resolve drilldown path {index + 1} '{path}': {ex.Message}", ex);
            }

            if (!last)
            {
                currentObjects = MaterializeDrillDown(current).Objects;
            }
        }

        return current;
    }

    /// <summary>
    ///     Runs an action in the render mode the objects it targets were rendered in.
    /// </summary>
    /// <remarks>
    ///     An action resolves its path by re-rendering the object it names, and a drilldown reads a
    ///     different type configuration from the main view. Left in the default mode, an action
    ///     triggered inside a drilldown would look for its property in the main view's render - so a
    ///     property the drilldown configuration is the only thing to expose, which is the ordinary
    ///     case for a popup, could be displayed and never edited or invoked.
    /// </remarks>
    private static OperationResponse InActionRenderMode(string[] objectPaths, Func<OperationResponse> action)
    {
        if (objectPaths == null || objectPaths.Length == 0)
        {
            return action();
        }

        DiagnosticRenderMode? previousMode = _renderMode.Value;
        _renderMode.Value = DiagnosticRenderMode.DrillDown;
        try
        {
            return action();
        }
        finally
        {
            _renderMode.Value = previousMode;
        }
    }

    /// <summary>
    ///     Narrows the objects an action runs against to the drilldown the operator has open.
    /// </summary>
    /// <remarks>
    ///     An empty chain means the main view, whose objects are the registered ones. A chain means
    ///     the action was triggered inside a drilldown, and the path it carries is relative to that
    ///     drilldown's own diagnostics — resolving it against the registered objects instead would
    ///     either miss or, worse, hit a same-named property on a different object.
    /// </remarks>
    private static IEnumerable<RegisteredObject> ResolveActionObjects(
        IEnumerable<RegisteredObject> registeredObjects,
        IReadOnlyList<string> objectPaths
    )
    {
        if (objectPaths == null || objectPaths.Count == 0)
        {
            return registeredObjects;
        }

        DrillDownTarget target = ResolveDrillDownTarget(registeredObjects, objectPaths, DrillDownAccess.Inspect);
        return MaterializeDrillDown(target).Objects;
    }

    /// <summary>
    ///     Separates an item's display name from the fence that identifies WHICH item it was.
    /// </summary>
    /// <remarks>
    ///     A unit separator, for the same reason the event-view id uses one: no name a host can
    ///     produce contains it, and it does not render. A client splits on it for display and
    ///     echoes the whole string back as a path.
    /// </remarks>
    internal const char ItemFenceSeparator = '';

    /// <summary>
    ///     Identifies the item an index referred to at the moment it was rendered.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An index alone is not an identity. A drilldown names items positionally, and an
    ///         action carrying that path is resolved by enumerating the collection again - so a
    ///         host that removed an earlier item in between leaves the same index pointing at a
    ///         DIFFERENT object, and the action lands on it silently. Only an index that runs off
    ///         the end fails on its own.
    ///     </para>
    ///     <para>
    ///         Carrying the identity in the name makes the mismatch a lookup failure, which is the
    ///         behaviour the rest of this path already promises: a chain that no longer resolves is
    ///         refused rather than answered with the wrong object. It is a fence, not a handle -
    ///         nothing is retained on the agent, and a stale one simply fails to match.
    ///     </para>
    ///     <para>
    ///         Reference identity for an object, because that is what "the same item" means for
    ///         one; value hash for a scalar, because a scalar is rewrapped on every render and its
    ///         reference would never match twice. Two equal scalars share a fence, which is correct
    ///         - they are interchangeable. A hash collision degrades to the behaviour that existed
    ///         before the fence rather than to something worse.
    ///     </para>
    /// </remarks>
    private static string ItemFence(object item)
    {
        if (item == null)
        {
            return "0";
        }

        // Reference identity only for an actual reference type. A drill-down-classified struct
        // (e.g. KeyValuePair<TKey, TValue>) is re-boxed on every enumeration, so
        // RuntimeHelpers.GetHashCode would mint a new fence for the same unchanged item on every
        // re-render; its own value-based GetHashCode is stable across boxings instead.
        int fence =
            IsDrillDownValue(item) && !item.GetType().IsValueType
                ? RuntimeHelpers.GetHashCode(item)
                : item.GetHashCode();
        return fence.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Turns the resolved value into the registered objects a render walks: one for a single
    ///     object, one per item for a collection.
    /// </summary>
    private static DrillDownMaterialization MaterializeDrillDown(DrillDownTarget target)
    {
        IEnumerable enumerable = target.Value is string ? null : target.Value as IEnumerable;
        if (enumerable == null)
        {
            Type type = target.Value.GetType();
            return new DrillDownMaterialization(
                [RegisteredObject.Derived(target.Value, "DrillDown", type.Name)],
                1,
                1,
                false
            );
        }

        int maxItems = target.MaxItems > 0 ? target.MaxItems : DrillDownMaxItems;
        List<object> items = enumerable.Cast<object>().Take(maxItems + 1).ToList();
        bool truncated = items.Count > maxItems;
        if (truncated)
        {
            items.RemoveAt(items.Count - 1);
        }

        List<RegisteredObject> registered = [];
        for (int index = 0; index < items.Count; index++)
        {
            // A scalar item has no properties of its own, so it is wrapped in something that does.
            // Registered as Derived: the wrapper exists only for this response, and held weakly it
            // could be collected between here and the render, leaving the item silently absent.
            object item = IsDrillDownValue(items[index]) ? items[index] : new DrillDownScalarValue(items[index]);
            string name = $"{target.ItemName ?? "Items"}[{index}]{ItemFenceSeparator}{ItemFence(items[index])}";
            registered.Add(RegisteredObject.Derived(item, "Items", name));
        }

        int? totalCount;
        if (enumerable is ICollection collection)
        {
            totalCount = collection.Count;
        }
        else
        {
            // Without a Count, the only honest total is the one we counted - and if we stopped at
            // the ceiling we did not reach the end, so there is no total to give.
            totalCount = truncated ? null : items.Count;
        }
        return new DrillDownMaterialization(registered, items.Count, totalCount, truncated);
    }

    private sealed class DrillDownTarget
    {
        public DrillDownTarget(object value, int maxItems, string itemName = null)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            MaxItems = maxItems;
            ItemName = itemName;
        }

        public object Value { get; }
        public int MaxItems { get; }
        public string ItemName { get; }
    }

    private sealed class DrillDownMaterialization
    {
        public DrillDownMaterialization(
            IReadOnlyList<RegisteredObject> registeredObjects,
            int displayedCount,
            int? totalCount,
            bool isTruncated
        )
        {
            Objects = registeredObjects;
            DisplayedCount = displayedCount;
            TotalCount = totalCount;
            IsTruncated = isTruncated;
        }

        public IReadOnlyList<RegisteredObject> Objects { get; }
        public int DisplayedCount { get; }
        public int? TotalCount { get; }
        public bool IsTruncated { get; }
    }

    [DiagnosticClass(AttributedPropertiesOnly = true)]
    private sealed class DrillDownScalarValue
    {
        public DrillDownScalarValue(object value) => Value = value;

        [DiagnosticProperty]
        public object Value { get; }
    }

    #endregion

    private sealed class PropIdent
    {
        public string BagCategory { get; private set; }
        public string BagName { get; private set; }
        public string PropCategory { get; private set; }
        public string PropName { get; private set; }

        public static PropIdent Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be empty.");
            }

            string[] elements = path.Split('|');
            if (elements.Length < 2 || string.IsNullOrEmpty(elements[0]) || string.IsNullOrEmpty(elements[1]))
            {
                throw new ArgumentException(
                    $"Invalid property/operation path: '{path}'. Path must contain at least a category and name, separated by '|'."
                );
            }

            PropIdent ident = new()
            {
                BagCategory = NullIfEmpty(elements.ElementAtOrDefault(0)),
                BagName = NullIfEmpty(elements.ElementAtOrDefault(1)),
                PropCategory = NullIfEmpty(elements.ElementAtOrDefault(2)),
                PropName = NullIfEmpty(elements.ElementAtOrDefault(3)),
            };
            return ident;
        }

        public override string ToString()
        {
            if (PropName != null)
            {
                return $"{BagCategory}|{BagName}|{PropCategory}|{PropName}";
            }

            if (PropCategory != null)
            {
                return $"{BagCategory}|{BagName}|{PropCategory}";
            }

            return $"{BagCategory}|{BagName}";
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    private static object ConvertValue(Type type, string value)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            type = type.GetGenericArguments()[0];
        }

        if (type.IsEnum)
        {
            return Enum.Parse(type, value, true);
        }

        try
        {
            return Convert.ChangeType(value, type);
        }
        catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
        {
            if (TryParseValue(type, value, out object parsed))
            {
                return parsed;
            }

            throw;
        }
    }

    private static bool TryParseValue(Type type, string value, out object parsed)
    {
        parsed = null;

        MethodInfo method = type.GetMethod("Parse", PublicStaticMethods, null, [typeof(string)], null);

        if (method == null)
        {
            return false;
        }

        try
        {
            parsed = method.Invoke(null, [value]);
            return true;
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
            throw;
        }
    }
}
