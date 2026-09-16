namespace Janet.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The non-generic face of <see cref="RegisterFactoryAttribute{TFactory, TCreates}"/>, so the
/// registration loop can drive an attribute whose type arguments it cannot name.
/// </summary>
/// <remarks>
/// The indirection exists so that <see cref="Register"/> runs <i>inside</i> the closed generic
/// attribute, where <c>TFactory</c> and <c>TCreates</c> are real type parameters and the call to
/// <see cref="IFactory{TCreates}.Create"/> is bound by the compiler. The previous shape resolved
/// Create reflectively by name, which meant renaming it compiled cleanly and failed only when the
/// container was first asked for the product — and failed outright for a factory that implemented
/// the interface explicitly, because the method is then private and name-mangled.
/// </remarks>
internal interface IFactoryRegistration
{
    /// <summary>The service type the factory itself is registered as.</summary>
    Type FactoryServiceType { get; }

    /// <summary>The type the factory produces.</summary>
    Type ProductType { get; }

    /// <summary>Registers the factory and its product, both as singletons.</summary>
    /// <param name="services">The collection to register into.</param>
    /// <param name="implementationType">The decorated class, which must implement
    /// <see cref="FactoryServiceType"/>; the caller checks that before calling this.</param>
    void Register(IServiceCollection services, Type implementationType);
}

/// <summary>
/// Marks a class as the one factory for <typeparamref name="TCreates"/>, and registers both the
/// factory and the product it builds.
/// </summary>
/// <typeparam name="TFactory">
/// The service type the factory is registered as, constrained to <see cref="IFactory{TCreates}"/>.
/// The decorated class must implement it — the constraint binds this type argument, not the class
/// carrying the attribute, so that is checked when the attribute is processed.
/// </typeparam>
/// <typeparam name="TCreates">The type the factory produces.</typeparam>
/// <remarks>
/// <para>
/// Reach for this when the container cannot build the product on its own: when construction means
/// composing several registered services into a particular arrangement rather than handing them to
/// a constructor — a chain of cache levels linked in order and exposed as its head, say, where what
/// consumers want injected is the head and the linking is the part no constructor expresses.
/// <see cref="RegisterDependencyAttribute"/> covers everything the container can build itself, and
/// is the right default.
/// </para>
/// <para>
/// <b>What gets registered.</b> Two singletons, when
/// <see cref="ServiceCollectionExtensions.AddDependenciesFromAttributes"/> scans the assembly;
/// nothing else discovers the attribute. <typeparamref name="TFactory"/> resolves to the decorated
/// class, and <typeparamref name="TCreates"/> resolves by calling
/// <see cref="IFactory{TCreates}.Create"/> on it. Consumers inject the product and never name the
/// factory.
/// </para>
/// <para>
/// <b>One owner, enforced.</b> Registration throws <see cref="InvalidOperationException"/> if the
/// decorated class does not implement <typeparamref name="TFactory"/>, or if
/// <typeparamref name="TCreates"/> is already registered by anything at all. Declaring a factory
/// settles where the product comes from, so a second registration is a contradiction rather than a
/// variant: both descriptors would land and the container would silently serve whichever came last,
/// in an order decided by <see cref="System.Reflection.Assembly.GetTypes"/> and therefore by
/// nothing the author controls.
/// </para>
/// <para>
/// <b>The lifetime is fixed at singleton</b>, deliberately, unlike
/// <see cref="RegisterDependencyAttribute.Lifetime"/>. A factory here exists to assemble something
/// once, so the product is built on first resolution and shared from then on. Holding the factory
/// itself as a singleton also makes a captive dependency unrepresentable rather than merely
/// discouraged. Per-scope products are the one case this forecloses; the knob belongs on the
/// product, not the factory, if it is ever needed.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [RegisterFactory&lt;IFactory&lt;IPipeline&gt;, IPipeline&gt;]
/// class PipelineFactory : IPipelineFactory
/// {
///     public IPipeline Create() =&gt; this.Head;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public class RegisterFactoryAttribute<TFactory, TCreates> : Attribute, IFactoryRegistration
    where TCreates : class
    where TFactory : IFactory<TCreates>
{
    Type IFactoryRegistration.FactoryServiceType => typeof(TFactory);

    Type IFactoryRegistration.ProductType => typeof(TCreates);

    void IFactoryRegistration.Register(IServiceCollection services, Type implementationType)
    {
        services.AddSingleton(typeof(TFactory), implementationType);

        // Create() is a compiler-bound call on TFactory, whose constraint guarantees it exists
        // and returns TCreates. Nothing here looks a method up by name.
        services.AddSingleton(
            typeof(TCreates),
            provider => ((TFactory)provider.GetRequiredService(typeof(TFactory))).Create());
    }
}
