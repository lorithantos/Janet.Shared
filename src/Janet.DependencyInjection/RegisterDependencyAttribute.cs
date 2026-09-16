namespace Janet.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Marks a class for registration by
/// <see cref="ServiceCollectionExtensions.AddDependenciesFromAttributes"/>. Use the generic form
/// <see cref="RegisterDependencyAttribute{TInterface}"/> to name the service type in the
/// declaration; this open form exists for the rare case where it is only known as a
/// <see cref="System.Type"/>.
/// </summary>
/// <remarks>
/// Repeatable, and that is how one implementation serves several interfaces: the first attribute
/// registers the class, and every later one registers a descriptor that forwards to it, so all of
/// them resolve to the <i>same</i> instance rather than to one object each.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class RegisterDependencyAttribute : Attribute
{
    /// <summary>
    /// The lifetime to register with, scoped unless set. Every attribute on one class must agree;
    /// registration throws rather than pick one, because the forwarding descriptors above only
    /// make sense when all of them share an instance.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>
    /// The service type to register as. When left null the class's first interface is used, and
    /// the class itself if it implements none — so a class with several interfaces and no explicit
    /// type registers as whichever the runtime lists first, which is not worth relying on.
    /// </summary>
    public Type? ServiceType { get; set; }
}

/// <summary>
/// The form to reach for: <c>[RegisterDependency&lt;IThing&gt;]</c> registers the class as
/// <typeparamref name="TInterface"/>, checked by the compiler rather than written as a
/// <see cref="System.Type"/>.
/// </summary>
/// <typeparam name="TInterface">The service type to register the decorated class as.</typeparam>
public class RegisterDependencyAttribute<TInterface> : RegisterDependencyAttribute
{
    /// <summary>Sets <see cref="RegisterDependencyAttribute.ServiceType"/> to
    /// <typeparamref name="TInterface"/>.</summary>
    public RegisterDependencyAttribute() => this.ServiceType = typeof(TInterface);
}
