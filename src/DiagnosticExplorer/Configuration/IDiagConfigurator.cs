using System.Linq.Expressions;
using System.Reflection;
using DiagnosticExplorer.Logging;

namespace DiagnosticExplorer;

public interface IDiagConfigurator
{
    bool ApplyAttributes { get; set; }
    int DrillDownMaxItems { get; set; }
    void RegisterObjects(Action<IDiagRegistrar> configure);
    void ConfigureHosting(Action<IDiagnosticHostingConfigurator> configure);
    ISystemEnvironmentConfigurator ConfigureSystemEnvironment();
    void ConfigureEventRouting(Action<EventSinkRouteOptions> configure);
    void ConfigureLogEventRetention(Action<LogEventRetentionOptions> configure);
    void DefaultFormat<T>(string formatString);
    void ConfigureAssemblies(params Assembly[] assemblies);
    void Configure<T>(Action<ITypeConfigurator<T>> configure);
    void ConfigureDrillDown<T>(Action<ITypeConfigurator<T>> configure);
}

public interface IDiagRegistrar : IServiceProvider
{
    void Register(object value, string category, string name);
    void RegisterService<TService>(string category, string name);
}

public interface IDiagnosticHostingConfigurator
{
    IDiagnosticHostingConfigurator Enabled(bool enabled = true);
    IDiagnosticHostingConfigurator AddHost(DiagnosticHostType type, string url);
    IDiagnosticHostingConfigurator EventRetention(Action<EventRetentionOptions> configure);
}

public interface ISystemEnvironmentConfigurator
{
    ISystemEnvironmentConfigurator Enabled(bool enabled = true);
    ISystemEnvironmentConfigurator WithCategory(string category);
    ISystemEnvironmentConfigurator WithName(string name);
}

public interface ITypeConfigurator<T>
{
    ITypeConfigurator<T> ExcludeAll();
    ITypeConfigurator<T> IncludeAll();
    ICategoryScope CreateCategoryScope(string category);
    ITypeConfigurator<T> Include<TProperty>(Expression<Func<T, TProperty>> property);
    ITypeConfigurator<T> Exclude<TProperty>(Expression<Func<T, TProperty>> property);
    IPropertyConfigurator<T, TProperty> Property<TProperty>(Expression<Func<T, TProperty>> property);
    IPropertyConfigurator<T, TProperty> Property<TProperty>(string name, Func<T, TProperty> value);
    ICustomPropertyConfigurator<T> Custom(string name, Action<ICustomObjectConfigurator<T>> configure);
    ITypeConfigurator<T> Route(string loggerName, LoggerNameMatchMode matchMode, Action<DrillDownEventRoute> configure);
    ITypeConfigurator<T> Route(
        Func<T, string> loggerName,
        LoggerNameMatchMode matchMode,
        Action<DrillDownEventRoute> configure
    );
}

public interface ICategoryScope : IDisposable
{
    ICategoryScope Expanded(bool expanded = true);
}

public interface IPropertyConfigurator
{
    IPropertyConfigurator WithLabel(string label);
    IPropertyConfigurator WithCategory(string category);
    IPropertyConfigurator Description(string description);
    IPropertyConfigurator Format(string formatString);
    IPropertyConfigurator AllowSet(bool allowSet = true);
}

public interface IObjectPropertyConfigurator<T, TSelf> : IPropertyConfigurator
{
    new TSelf WithLabel(string label);
    TSelf WithLabel(Func<T, string> label);
    new TSelf WithCategory(string category);
    TSelf WithCategory(Func<T, string> category);
    new TSelf Description(string description);
    TSelf Description(Func<T, string> description);
    new TSelf Format(string formatString);
    new TSelf AllowSet(bool allowSet = true);
}

public interface IPropertyConfigurator<T, TProperty>
    : IObjectPropertyConfigurator<T, IPropertyConfigurator<T, TProperty>>
{
    IPropertyConfigurator<T, TProperty> Format(Func<TProperty, string> format);
    IPropertyConfigurator<T, TProperty> WithText(string text);
    IPropertyConfigurator<T, TProperty> WithText(Func<T, string> text);
    IPropertyConfigurator<T, TProperty> WithIconSize(StatusIconSize size);
    IPropertyConfigurator<T, TProperty> AsJson(int maxLength = 100);
    IPropertyConfigurator<T, TProperty> AsDateOnly();
    IPropertyConfigurator<T, TProperty> WithDrillDown(bool enabled = true, int? maxItems = null);
    IPropertyConfigurator<T, TProperty> WithDrillDownOnly(int? maxItems = null);
    IPropertyConfigurator<T, TProperty> WithDrillDownOnly(string text, int? maxItems = null);
    IPropertyConfigurator<T, TProperty> WithDrillDownOnly(Func<T, string> text, int? maxItems = null);
    IPropertyConfigurator<T, TProperty> WithJsonHover(bool enabled = true);
    IPropertyConfigurator<T, TProperty> WithExpandedHover(bool enabled = true);
    IPropertyConfigurator<T, TProperty> WithStatus(StatusCode status, Func<T, bool> condition);
    IPropertyConfigurator<T, TProperty> WithStatus(StatusCode status, Func<T, bool> condition, string text);
    IPropertyConfigurator<T, TProperty> WithStatus(StatusCode status, Func<T, bool> condition, Func<T, string> text);
    IPropertyConfigurator<T, TProperty> Warn(Func<T, bool> condition);
    IPropertyConfigurator<T, TProperty> Warn(Func<T, bool> condition, string message);
    IPropertyConfigurator<T, TProperty> Warn(Func<T, bool> condition, string message, string category);
    IPropertyConfigurator<T, TProperty> Warn(Func<T, bool> condition, Func<T, string> message);
    IPropertyConfigurator<T, TProperty> Warn(Func<T, bool> condition, Func<T, string> message, string category);
    IPropertyConfigurator<T, TProperty> Error(Func<T, bool> condition);
    IPropertyConfigurator<T, TProperty> Error(Func<T, bool> condition, string message);
    IPropertyConfigurator<T, TProperty> Error(Func<T, bool> condition, string message, string category);
    IPropertyConfigurator<T, TProperty> Error(Func<T, bool> condition, Func<T, string> message);
    IPropertyConfigurator<T, TProperty> Error(Func<T, bool> condition, Func<T, string> message, string category);
}

public interface ICustomPropertyConfigurator<T>
{
    ICustomPropertyConfigurator<T> AsJson(int maxLength = 100);
    ICustomPropertyConfigurator<T> Expand(bool initiallyExpanded = true);
    ICustomPropertyConfigurator<T> WithDrillDown(bool enabled = true, int? maxItems = null);
    ICustomPropertyConfigurator<T> WithDrillDownOnly(int? maxItems = null);
    ICustomPropertyConfigurator<T> WithDrillDownOnly(string text, int? maxItems = null);
    ICustomPropertyConfigurator<T> WithDrillDownOnly(Func<T, string> text, int? maxItems = null);
    ICustomPropertyConfigurator<T> WithJsonHover(bool enabled = true);
    ICustomPropertyConfigurator<T> WithExpandedHover(bool enabled = true);
    ICustomPropertyConfigurator<T> WithCategory(string category);
    ICustomPropertyConfigurator<T> WithCategory(Func<T, string> category);
    ICustomPropertyConfigurator<T> Description(string description);
    ICustomPropertyConfigurator<T> Description(Func<T, string> description);
    ICustomPropertyConfigurator<T> WithStatus(StatusCode status, Func<T, bool> condition);
    ICustomPropertyConfigurator<T> WithStatus(StatusCode status, Func<T, bool> condition, string text);
    ICustomPropertyConfigurator<T> WithStatus(StatusCode status, Func<T, bool> condition, Func<T, string> text);
    ICustomPropertyConfigurator<T> Warn(Func<T, bool> condition);
    ICustomPropertyConfigurator<T> Warn(Func<T, bool> condition, string message);
    ICustomPropertyConfigurator<T> Warn(Func<T, bool> condition, string message, string category);
    ICustomPropertyConfigurator<T> Warn(Func<T, bool> condition, Func<T, string> message);
    ICustomPropertyConfigurator<T> Warn(Func<T, bool> condition, Func<T, string> message, string category);
    ICustomPropertyConfigurator<T> Error(Func<T, bool> condition);
    ICustomPropertyConfigurator<T> Error(Func<T, bool> condition, string message);
    ICustomPropertyConfigurator<T> Error(Func<T, bool> condition, string message, string category);
    ICustomPropertyConfigurator<T> Error(Func<T, bool> condition, Func<T, string> message);
    ICustomPropertyConfigurator<T> Error(Func<T, bool> condition, Func<T, string> message, string category);
}

public interface ICustomObjectConfigurator<T>
{
    IPropertyConfigurator<T, TProperty> Property<TProperty>(Expression<Func<T, TProperty>> property);
    IPropertyConfigurator<T, TProperty> Property<TProperty>(string name, Func<T, TProperty> value);
}

public interface ICollectionConfigurator<T, TItem> : IObjectPropertyConfigurator<T, ICollectionConfigurator<T, TItem>>
{
    ICollectionConfigurator<T, TItem> ShowCount(string name = null);
    ICollectionConfigurator<T, TItem> ConcatItems(string separator = null, Func<TItem, string> format = null);
    ICollectionConfigurator<T, TItem> ConcatItems(Func<TItem, string> format);
    ICollectionConfigurator<T, TItem> ListItems(Action<ICollectionListConfigurator<TItem>> configure = null);
    ICollectionConfigurator<T, TItem> ExpandItems(
        Action<ICollectionExpandedItemConfigurator<TItem>> configure = null,
        string name = null
    );
    ICollectionConfigurator<T, TItem> WithPrimaryPropertiesOnly();
    ICollectionConfigurator<T, TItem> WithMaxItems(int maxItems);
    ICollectionConfigurator<T, TItem> WithTextWrap();
    ICollectionConfigurator<T, TItem> WithDrillDown(bool enabled = true, int? maxItems = null);
    ICollectionConfigurator<T, TItem> WithDrillDownOnly(int? maxItems = null);
    ICollectionConfigurator<T, TItem> WithDrillDownOnly(string text, int? maxItems = null);
    ICollectionConfigurator<T, TItem> WithDrillDownOnly(Func<T, string> text, int? maxItems = null);
    ICollectionConfigurator<T, TItem> WithJsonHover(bool enabled = true);
    ICollectionConfigurator<T, TItem> WithExpandedHover(bool enabled = true);
}

public interface ICollectionListConfigurator<TItem>
{
    ICollectionListConfigurator<TItem> WithName(Func<TItem, string> format);
    ICollectionListConfigurator<TItem> WithName(Func<TItem, int, string> format);
    ICollectionListConfigurator<TItem> WithValue(Func<TItem, string> format);
    ICollectionListConfigurator<TItem> AsJson(int maxLength = 8092);
    ICollectionListConfigurator<TItem> Wide(int? cols = null);
    ICollectionListConfigurator<TItem> WithDrillDown(bool enabled = true, int? maxItems = null);
    ICollectionListConfigurator<TItem> WithJsonHover(bool enabled = true);
    ICollectionListConfigurator<TItem> WithExpandedHover(bool enabled = true);
    ICollectionListConfigurator<TItem> WithDescription(Func<TItem, string> format);
    ICollectionListConfigurator<TItem> WithCategory(Func<TItem, string> format);
    ICollectionListConfigurator<TItem> WithStatus(StatusCode status, Func<TItem, bool> condition);
    ICollectionListConfigurator<TItem> WithStatus(StatusCode status, Func<TItem, bool> condition, string text);
    ICollectionListConfigurator<TItem> WithStatus(
        StatusCode status,
        Func<TItem, bool> condition,
        Func<TItem, string> text
    );
}

public interface ICollectionExpandedItemConfigurator<TItem>
{
    ICollectionExpandedItemConfigurator<TItem> WithName(Func<TItem, string> format);
    ICollectionExpandedItemConfigurator<TItem> WithInitiallyExpanded();
    ICollectionExpandedItemConfigurator<TItem> WithInitiallyCollapsed();
    ICollectionExpandedItemConfigurator<TItem> WithPrimaryPropertiesOnly();
    ICollectionExpandedItemConfigurator<TItem> WithStatus(StatusCode status, Func<TItem, bool> condition);
    ICollectionExpandedItemConfigurator<TItem> WithStatus(StatusCode status, Func<TItem, bool> condition, string text);
    ICollectionExpandedItemConfigurator<TItem> WithStatus(
        StatusCode status,
        Func<TItem, bool> condition,
        Func<TItem, string> text
    );
    ICollectionExpandedItemConfigurator<TItem> WithIconSize(StatusIconSize size);
    ICollectionExpandedItemConfigurator<TItem> WithDrillDown(bool enabled = true, int? maxItems = null);
}
