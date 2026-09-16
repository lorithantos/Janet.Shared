namespace Janet.Shared.Tests;

using Janet.DependencyInjection;
using Janet.DependencyInjection.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// The public entry point, over a fixture assembly that carries only valid declarations.
/// <para>
/// The unit tests next door drive the three passes by hand, one type at a time; what they cannot
/// show is that a single scan runs them in an order where each pass has what it needs. That is
/// the claim here, and it is the one the remarks call load-bearing.
/// </para>
/// </summary>
public class AddDependenciesFromAttributesTests
{
    private static ServiceProvider BuildFromFixture()
    {
        ServiceCollection services = new();
        services.AddDependenciesFromAttributes(typeof(Telemetry).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Scan_RegistersEveryDeclaredService()
    {
        using ServiceProvider provider = BuildFromFixture();

        Assert.IsType<Telemetry>(provider.GetRequiredService<IReporter>());
        Assert.IsType<Pump>(provider.GetRequiredService<IPump>());
        Assert.IsType<PipelineFactory>(provider.GetRequiredService<IFactory<IPipeline>>());
    }

    [Fact]
    public void Scan_ForwardsTheSecondInterfaceToTheSameInstance()
    {
        using ServiceProvider provider = BuildFromFixture();

        Assert.Same(provider.GetRequiredService<IReporter>(), provider.GetRequiredService<IProbe>());
    }

    /// <summary>
    /// The hosted-service pass forwards to a registration the dependency pass must already have
    /// made. Run the other way round it has nothing to point at, and the scan throws.
    /// </summary>
    [Fact]
    public void Scan_StartsTheSameInstanceConsumersInject()
    {
        using ServiceProvider provider = BuildFromFixture();

        Assert.Same(provider.GetRequiredService<IPump>(), Assert.Single(provider.GetServices<IHostedService>()));
    }

    /// <summary>
    /// The factory pass refuses a product that any earlier pass registered, so it has to run last;
    /// this is the assertion that the product still resolves, and that it comes from Create.
    /// </summary>
    [Fact]
    public void Scan_ResolvesTheProductThroughItsFactory()
    {
        using ServiceProvider provider = BuildFromFixture();

        IPipeline pipeline = provider.GetRequiredService<IPipeline>();

        Assert.Equal("assembled by the factory", pipeline.Assembled);
        Assert.Same(pipeline, provider.GetRequiredService<IPipeline>());
    }

    [Fact]
    public void Scan_OfAnAssemblyWithNoAttributes_RegistersNothing()
    {
        ServiceCollection services = new();

        services.AddDependenciesFromAttributes(typeof(Assert).Assembly);

        Assert.Empty(services);
    }

    [Fact]
    public void Scan_ReturnsTheSameCollectionForChaining()
    {
        ServiceCollection services = new();

        IServiceCollection returned = services.AddDependenciesFromAttributes(typeof(Telemetry).Assembly);

        Assert.Same(services, returned);
    }
}
