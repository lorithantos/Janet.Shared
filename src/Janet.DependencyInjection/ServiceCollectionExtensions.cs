namespace Janet.DependencyInjection;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Turns the registration attributes in an assembly into container registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every class in <paramref name="assembly"/> carrying
    /// <see cref="RegisterDependencyAttribute"/>, <see cref="RegisterHostedServiceAttribute"/> or
    /// <see cref="RegisterFactoryAttribute{TFactory, TCreates}"/>, in that order.
    /// </summary>
    /// <param name="services">The collection to register into.</param>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Call it once per assembly that carries the attributes — an application with its services in
    /// a library of their own calls it twice, once for each. The order of the three passes is
    /// load-bearing, in opposite directions: the hosted-service pass forwards to a registration the
    /// dependency pass must already have made, while the factory pass refuses a product type that
    /// any earlier pass registered. Both checks run while registering, so a declaration that cannot
    /// be honoured throws during startup — only the factory's product is resolved lazily, and by
    /// then its declaration has already been validated.
    /// </remarks>
    public static IServiceCollection AddDependenciesFromAttributes(this IServiceCollection services, Assembly assembly)
    {
        Type[] types = assembly.GetTypes();

        List<Type> dependencyAttributeTypes = [.. types.Where(t => t.GetCustomAttributes<RegisterDependencyAttribute>().Any())];
        foreach (Type type in dependencyAttributeTypes)
        {
            HandleRegisterDependencyAttribute(services, type);
        }

        List<Type> hostedServiceTypes = [.. types.Where(t => t.GetCustomAttribute<RegisterHostedServiceAttribute>() != null)];
        foreach (Type type in hostedServiceTypes)
        {
            HandleRegisterHostedServiceAttribute(services, type);
        }

        List<Type> factoryAttributeTypes = [.. types.Where(t => t.GetCustomAttributes().OfType<IFactoryRegistration>().Any())];
        foreach (Type type in factoryAttributeTypes)
        {
            HandleRegisterFactoryAttribute(services, type);
        }

        return services;
    }

    internal static void HandleRegisterDependencyAttribute(IServiceCollection services, Type type)
    {
        RegisterDependencyAttribute[] attributes = [.. type.GetCustomAttributes<RegisterDependencyAttribute>()];
        if (attributes.Length == 0)
        {
            return;
        }

        ServiceLifetime lifetime = attributes[0].Lifetime;
        if (attributes.Any(a => a.Lifetime != lifetime))
        {
            throw new InvalidOperationException(
                $"Type {type.FullName} has multiple [{nameof(RegisterDependencyAttribute)}] attributes with different Lifetime values. All attributes on the same type must use the same Lifetime.");
        }

        Type primaryServiceType = attributes[0].ServiceType ?? type.GetInterfaces().FirstOrDefault() ?? type;

        services.Add(new ServiceDescriptor(primaryServiceType, type, lifetime));

        // Every service type after the first forwards to the primary rather than registering the
        // implementation again, which is what makes one class serving three interfaces resolve to
        // one object instead of three.
        for (int i = 1; i < attributes.Length; i++)
        {
            Type additionalServiceType = attributes[i].ServiceType ?? throw new InvalidOperationException(
                $"Type {type.FullName} has multiple [{nameof(RegisterDependencyAttribute)}] attributes but attribute at index {i} has no ServiceType.");
            services.Add(new ServiceDescriptor(additionalServiceType, sp => sp.GetRequiredService(primaryServiceType), lifetime));
        }
    }

    internal static void HandleRegisterFactoryAttribute(IServiceCollection services, Type type)
    {
        IFactoryRegistration? registration = type.GetCustomAttributes().OfType<IFactoryRegistration>().FirstOrDefault();

        if (registration == null)
        {
            return;
        }

        // The attribute's constraint ties TFactory to IFactory<TCreates>, but nothing makes the
        // DECORATED class implement it -- that is what this checks.
        if (!registration.FactoryServiceType.IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"Type {type.FullName} has the RegisterFactoryAttribute naming {registration.FactoryServiceType.FullName} but does not implement it.");
        }

        // A product type has exactly one owner. Two factories claiming one product both register,
        // and the container silently serves whichever came last -- an order decided by
        // Assembly.GetTypes(), which is not guaranteed. Refuse instead of picking one.
        if (services.Any(descriptor => descriptor.ServiceType == registration.ProductType))
        {
            throw new InvalidOperationException(
                $"Type {type.FullName} is registered as a factory for {registration.ProductType.FullName}, which is already registered. A product type must have exactly one factory and no other registration.");
        }

        registration.Register(services, type);
    }

    internal static void HandleRegisterHostedServiceAttribute(IServiceCollection services, Type type)
    {
        if (!typeof(IHostedService).IsAssignableFrom(type))
        {
            throw new InvalidOperationException(
                $"Type {type.FullName} has [{nameof(RegisterHostedServiceAttribute)}] but does not implement IHostedService.");
        }

        RegisterDependencyAttribute depAttr = type.GetCustomAttributes<RegisterDependencyAttribute>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Type {type.FullName} has [{nameof(RegisterHostedServiceAttribute)}] but no [{nameof(RegisterDependencyAttribute)}].");

        // A hosted service is resolved from the ROOT provider, so singleton is the only lifetime
        // the forwarding below can honour -- and Lifetime defaults to Scoped, which makes the
        // plainest possible declaration the broken one. Refused here rather than left to fail at
        // Host.StartAsync, where scope validation turns it into a resolve-time throw a long way
        // from the declaration, and where validation being off turns it into a captive dependency
        // that throws nothing at all.
        if (depAttr.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                $"Type {type.FullName} has [{nameof(RegisterHostedServiceAttribute)}] with a {depAttr.Lifetime} [{nameof(RegisterDependencyAttribute)}]. A hosted service is resolved from the root provider, so its registration must be {nameof(ServiceLifetime.Singleton)}: set Lifetime = ServiceLifetime.{nameof(ServiceLifetime.Singleton)}.");
        }

        // IHostedService is skipped when picking the primary type, because a class implementing
        // both its own interface and IHostedService would otherwise forward IHostedService to
        // itself and the host would start a different instance from the one consumers inject.
        Type primaryServiceType = depAttr.ServiceType ?? type.GetInterfaces().FirstOrDefault(i => i != typeof(IHostedService)) ?? type;

        services.AddSingleton<IHostedService>(sp => (IHostedService)sp.GetRequiredService(primaryServiceType));
    }
}
