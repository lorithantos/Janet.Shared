namespace Janet.Shared.Tests;

using Janet.Wpf.Testing;

/// <summary>
/// The application these tests run against: an STA thread, one
/// <see cref="System.Windows.Application"/>, and nothing merged into it.
///
/// <para>Nothing is pre-merged on purpose. <c>ThemeDictionaryTests</c> loads the
/// dictionaries itself — loading is its assertion — <c>ThemeServiceTests</c>
/// manipulates <c>MergedDictionaries</c> directly and puts it back, and
/// <c>GridSortMemoryTests</c> only needs the thread. A dictionary merged here
/// would be state those tests did not ask for and have to work around.</para>
///
/// <para>The subclass exists because xUnit constructs a collection fixture itself
/// and so needs a genuinely parameterless constructor; the base constructor's
/// parameters are all optional, so the implicit <c>base()</c> binds.</para>
/// </summary>
public sealed class JanetWpfFixture : WpfApplicationFixture;
