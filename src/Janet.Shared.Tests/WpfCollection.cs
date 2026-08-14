namespace Janet.Shared.Tests;

using Xunit;

/// <summary>
/// Everything that touches WPF runs in one collection, serially.
///
/// <para>Two reasons, both real. There is one STA thread and one
/// <see cref="System.Windows.Application"/> for the process, so parallel classes
/// would contend for the same dispatcher. And <c>ThemeService</c> is deliberately
/// static — it swaps a dictionary in <c>Application.Current.Resources</c> — so a
/// theme test running beside a style test would change the answer underneath
/// it.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "wpf";
}
