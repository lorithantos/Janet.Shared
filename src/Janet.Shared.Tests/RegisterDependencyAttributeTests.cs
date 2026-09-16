namespace Janet.Shared.Tests;

using Janet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// The registration attributes' contract, pinned one type at a time.
/// <para>
/// These drive the internal Handle* passes rather than
/// <see cref="ServiceCollectionExtensions.AddDependenciesFromAttributes"/>, because every
/// deliberately-broken type below lives in this assembly and a scan of the assembly would hit
/// them all. The whole-assembly path is pinned separately, over a fixture assembly that contains
/// only declarations meant to succeed.
/// </para>
/// </summary>
public class RegisterDependencyAttributeTests
{
    // -- Shared test types --

    interface IServiceA { }
    interface IServiceB { }
    interface IServiceC { }

    [RegisterDependency<IServiceA>(Lifetime = ServiceLifetime.Singleton),
     RegisterDependency<IServiceB>(Lifetime = ServiceLifetime.Singleton)]
    class MultiInterfaceService : IServiceA, IServiceB { }

    [RegisterDependency<IServiceA>(Lifetime = ServiceLifetime.Singleton),
     RegisterDependency<IServiceB>(Lifetime = ServiceLifetime.Transient)]
    class MismatchedLifetimeService : IServiceA, IServiceB { }

    [RegisterDependency<IServiceA>(Lifetime = ServiceLifetime.Singleton),
     RegisterDependency<IServiceB>(Lifetime = ServiceLifetime.Singleton),
     RegisterDependency<IServiceC>(Lifetime = ServiceLifetime.Singleton)]
    class TripleInterfaceService : IServiceA, IServiceB, IServiceC { }

    [RegisterDependency<IServiceA>(Lifetime = ServiceLifetime.Scoped)]
    class SingleInterfaceService : IServiceA { }

    /// <summary>Names no lifetime, so the attribute's default is the only thing deciding it.</summary>
    [RegisterDependency<IServiceA>]
    class UndeclaredLifetimeService : IServiceA { }

    // -- Factory test types --

    interface IProduct { }

    class Product : IProduct { }

    interface IProductFactory : IFactory<IProduct> { }

    [RegisterFactory<IFactory<IProduct>, IProduct>]
    class ProductFactory : IProductFactory
    {
        public IProduct Create() => new Product();
    }

    [RegisterFactory<IFactory<IProduct>, IProduct>]
    class RivalProductFactory : IProductFactory
    {
        public IProduct Create() => new Product();
    }

    /// <summary>Carries the attribute without implementing the interface it names.</summary>
    [RegisterFactory<IFactory<IProduct>, IProduct>]
    class NotAFactory { }

    // -- Hosted-service test types --

    interface IWorker { }

    [RegisterDependency<IWorker>(Lifetime = ServiceLifetime.Singleton), RegisterHostedService]
    class Worker : IWorker, IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Marked as hosted without implementing the interface that makes it one.</summary>
    [RegisterDependency<IWorker>(Lifetime = ServiceLifetime.Singleton), RegisterHostedService]
    class NotHosted : IWorker { }

    /// <summary>Hosted, but nothing registers the instance the forwarding would point at.</summary>
    [RegisterHostedService]
    class UndeclaredWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Hosted with the lifetime left at its default, which is Scoped — the plainest declaration
    /// anyone would write, and the broken one.
    /// </summary>
    [RegisterDependency<IWorker>, RegisterHostedService]
    class DefaultLifetimeWorker : IWorker, IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // -- Tests --

    [Fact]
    public void MultipleAttributes_SameLifetime_ResolvesToSameInstance()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(MultiInterfaceService));

        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceA a = provider.GetRequiredService<IServiceA>();
        IServiceB b = provider.GetRequiredService<IServiceB>();

        Assert.Same(a, b);
    }

    [Fact]
    public void MultipleAttributes_DifferentLifetimes_ThrowsOnRegistration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(MismatchedLifetimeService)));

        Assert.Contains("different Lifetime values", ex.Message);
    }

    [Fact]
    public void TripleAttributes_AllResolveToSameInstance()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(TripleInterfaceService));

        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceA a = provider.GetRequiredService<IServiceA>();
        IServiceB b = provider.GetRequiredService<IServiceB>();
        IServiceC c = provider.GetRequiredService<IServiceC>();

        Assert.Same(a, b);
        Assert.Same(b, c);
    }

    [Fact]
    public void SingleAttribute_RegistersNormally()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(SingleInterfaceService));

        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceA a = provider.GetRequiredService<IServiceA>();

        Assert.IsType<SingleInterfaceService>(a);
    }

    /// <summary>
    /// Scoped is the attribute's default, and the type under test names no lifetime at all --
    /// pinning it against a declaration that says <c>Scoped</c> would pass whatever the default
    /// became, which is how the first version of this test was wrong.
    /// </summary>
    [Fact]
    public void Lifetime_DefaultsToScoped()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(UndeclaredLifetimeService));

        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services).Lifetime);
    }

    [Fact]
    public void Factory_RegistersBothItselfAndItsProduct()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterFactoryAttribute(services, typeof(ProductFactory));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<ProductFactory>(provider.GetRequiredService<IFactory<IProduct>>());
        Assert.IsType<Product>(provider.GetRequiredService<IProduct>());
    }

    /// <summary>
    /// The product must come from the factory's own <c>Create</c>, not from the container
    /// constructing it: a resolver wired to the wrong thing would still hand back an IProduct.
    /// </summary>
    [Fact]
    public void Factory_ProductComesFromCreate()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterFactoryAttribute(services, typeof(ProductFactory));

        using ServiceProvider provider = services.BuildServiceProvider();
        IFactory<IProduct> factory = provider.GetRequiredService<IFactory<IProduct>>();

        Assert.Same(provider.GetRequiredService<IProduct>(), provider.GetRequiredService<IProduct>());
        Assert.NotSame(factory.Create(), provider.GetRequiredService<IProduct>());
    }

    [Fact]
    public void TwoFactoriesForOneProduct_ThrowsOnRegistration()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterFactoryAttribute(services, typeof(ProductFactory));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterFactoryAttribute(services, typeof(RivalProductFactory)));

        Assert.Contains("already registered", ex.Message);
        Assert.Contains(nameof(RivalProductFactory), ex.Message);
    }

    [Fact]
    public void FactoryNotImplementingTheInterfaceItNames_ThrowsOnRegistration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterFactoryAttribute(services, typeof(NotAFactory)));

        Assert.Contains("does not implement", ex.Message);
        Assert.Contains(nameof(NotAFactory), ex.Message);
    }

    /// <summary>
    /// The forwarding is the whole point: the host must start the object consumers inject, not a
    /// second one of the same type. Registering the implementation twice would satisfy both
    /// resolutions and pass everything except this.
    /// </summary>
    [Fact]
    public void HostedService_ForwardsToThePrimaryInstance()
    {
        ServiceCollection services = new();
        ServiceCollectionExtensions.HandleRegisterDependencyAttribute(services, typeof(Worker));
        ServiceCollectionExtensions.HandleRegisterHostedServiceAttribute(services, typeof(Worker));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IWorker>(), Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public void HostedServiceNotImplementingIHostedService_ThrowsOnRegistration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterHostedServiceAttribute(services, typeof(NotHosted)));

        Assert.Contains("does not implement IHostedService", ex.Message);
        Assert.Contains(nameof(NotHosted), ex.Message);
    }

    /// <summary>
    /// The host resolves hosted services from the root provider, so a non-singleton primary
    /// cannot be forwarded to. Registration is where that has to be said: left to run, it throws
    /// at Host.StartAsync under scope validation and captures a root-scoped instance silently
    /// without it — and the lifetime that produces it is the one nobody typed.
    /// </summary>
    [Fact]
    public void HostedServiceWithNonSingletonLifetime_ThrowsOnRegistration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterHostedServiceAttribute(services, typeof(DefaultLifetimeWorker)));

        Assert.Contains(nameof(ServiceLifetime.Scoped), ex.Message);
        Assert.Contains(nameof(ServiceLifetime.Singleton), ex.Message);
        Assert.Contains(nameof(DefaultLifetimeWorker), ex.Message);
    }

    [Fact]
    public void HostedServiceWithoutRegisterDependency_ThrowsOnRegistration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ServiceCollectionExtensions.HandleRegisterHostedServiceAttribute(services, typeof(UndeclaredWorker)));

        Assert.Contains(nameof(RegisterDependencyAttribute), ex.Message);
        Assert.Contains(nameof(UndeclaredWorker), ex.Message);
    }
}
