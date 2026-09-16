namespace Janet.DependencyInjection;

/// <summary>
/// Builds instances of <typeparamref name="TCreates"/>. The service type a
/// <see cref="RegisterFactoryAttribute{TFactory, TCreates}"/> names is constrained to this,
/// so the registration can call <see cref="Create"/> as a compiler-bound call rather than
/// finding a method by name.
/// </summary>
/// <typeparam name="TCreates">The type the factory creates.</typeparam>
public interface IFactory<TCreates>
{
    /// <summary>Creates and returns an instance of <typeparamref name="TCreates"/>.</summary>
    /// <returns>A new or cached instance of <typeparamref name="TCreates"/>.</returns>
    TCreates Create();
}
