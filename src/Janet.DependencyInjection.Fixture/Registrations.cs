namespace Janet.DependencyInjection.Fixture;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Everything in this assembly is a declaration that must survive a whole-assembly scan, and
/// nothing else belongs here. The three attributes appear once each, in the arrangement whose
/// pass ordering is load-bearing: the hosted-service forwarding needs a registration the
/// dependency pass has already made, and the factory refuses a product any earlier pass claimed.
/// </summary>
public interface IReporter
{
}

/// <summary>The second face of the same object, so the scan has a forwarding descriptor to make.</summary>
public interface IProbe
{
}

/// <summary>One implementation serving two interfaces.</summary>
[RegisterDependency<IReporter>(Lifetime = ServiceLifetime.Singleton),
 RegisterDependency<IProbe>(Lifetime = ServiceLifetime.Singleton)]
public class Telemetry : IReporter, IProbe
{
}

/// <summary>The service a consumer injects, whose instance the host must also be the one to start.</summary>
public interface IPump
{
}

/// <summary>Registered as <see cref="IPump"/> and additionally forwarded as a hosted service.</summary>
[RegisterDependency<IPump>(Lifetime = ServiceLifetime.Singleton), RegisterHostedService]
public class Pump : IPump, IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A product the container cannot build itself, so a factory owns it.</summary>
public interface IPipeline
{
    /// <summary>Distinguishes the assembled instance from a default-constructed one.</summary>
    string Assembled { get; }
}

/// <summary>The only thing that builds an <see cref="IPipeline"/>.</summary>
[RegisterFactory<IFactory<IPipeline>, IPipeline>]
public class PipelineFactory : IFactory<IPipeline>
{
    /// <inheritdoc/>
    public IPipeline Create() => new Pipeline("assembled by the factory");

    private sealed record Pipeline(string Assembled) : IPipeline;
}
