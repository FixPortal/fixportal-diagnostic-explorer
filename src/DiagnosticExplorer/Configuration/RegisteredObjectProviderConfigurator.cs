namespace DiagnosticExplorer;

internal sealed class RegisteredObjectProviderConfigurator : IDiagRegistrar
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICollection<RegisteredObject> _registeredObjects;

    public RegisteredObjectProviderConfigurator(
        IServiceProvider serviceProvider,
        ICollection<RegisteredObject> registeredObjects
    )
    {
        _serviceProvider = serviceProvider;
        _registeredObjects = registeredObjects ?? throw new ArgumentNullException(nameof(registeredObjects));
    }

    public object GetService(Type serviceType) => _serviceProvider?.GetService(serviceType);

    public void RegisterService<TService>(string category, string name)
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException(
                "RegisterService requires an IServiceProvider from DI hosting. Use RegisterObjects/Register with explicit objects for non-DI startup."
            );
        }

        object service = GetService(typeof(TService));
        if (service == null)
        {
            throw new InvalidOperationException($"No service for type '{typeof(TService)}' has been registered.");
        }

        Register(service, category, name);
    }

    public void Register(object value, string category, string name)
    {
        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("A category is required.", nameof(category));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A name is required.", nameof(name));
        }

        _registeredObjects.Add(RegisteredObject.Derived(value, category, name));
    }
}
