namespace Janet.DependencyInjection;

/// <summary>
/// Marks a class for additional registration as
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. The class must also carry a
/// <see cref="RegisterDependencyAttribute"/> and implement the interface. The hosted-service
/// registration forwards to the primary service instance, so the thing the host starts and the
/// thing consumers inject are the same object rather than two that merely share a type.
/// </summary>
/// <remarks>
/// The accompanying <see cref="RegisterDependencyAttribute"/> must name
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>, and
/// registration throws otherwise. A hosted service is resolved from the root provider, so nothing
/// else can be forwarded to: scoped resolves from the root — a captive dependency, and a throw
/// under scope validation — and transient would hand the host an instance of its own, which is
/// the one thing this attribute exists to prevent. Note that
/// <see cref="RegisterDependencyAttribute.Lifetime"/> defaults to
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>, so the lifetime
/// has to be written out here rather than left to the default.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public class RegisterHostedServiceAttribute : Attribute
{
}
