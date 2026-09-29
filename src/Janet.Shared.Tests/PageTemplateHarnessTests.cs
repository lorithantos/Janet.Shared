namespace Janet.Shared.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Janet.Wpf.Testing;
using Xunit;

/// <summary>
/// The harness, tested against itself.
///
/// <para>A binding failure is a line on a trace source and nothing else — no
/// throw, no build break, no missing window. So a harness that reports nothing
/// looks exactly like a page with no binding errors, and the only way to tell the
/// two apart is to point it at a binding that is known to be broken and watch it
/// fire. That is what the first two tests here are: the positive half proves the
/// detector can fire, and the negative half proves it is not simply firing all the
/// time.</para>
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PageTemplateHarnessTests
{
    private readonly JanetWpfFixture wpf;

    public PageTemplateHarnessTests(JanetWpfFixture wpf) => this.wpf = wpf;

    /// <summary>
    /// The lifted mutation check. A binding to a property nothing defines is
    /// reported, and reported by name — a failure list that merely said "something
    /// went wrong" would not tell anyone which binding to go and fix.
    /// </summary>
    [Fact]
    public void ABindingToAPropertyThatDoesNotExistIsReportedByName()
    {
        var failures = this.wpf.Run(() =>
        {
            var text = new TextBlock();
            text.SetBinding(TextBlock.TextProperty, new Binding("ThisPropertyDoesNotExist"));

            return PageTemplateHarness.RealiseElement(text, new Payload("anything"));
        });

        Assert.NotEmpty(failures);
        Assert.Contains(
            failures, f => f.Contains("ThisPropertyDoesNotExist", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half, and it is not decoration: without it, a detector that
    /// returned failures unconditionally — or one still holding a listener from an
    /// earlier realisation — would satisfy the test above and every page would
    /// "fail" for reasons nobody could act on.
    /// </summary>
    [Fact]
    public void ABindingToAPropertyThatExistsIsNotReported()
    {
        var failures = this.wpf.Run(() =>
        {
            var text = new TextBlock();
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Payload.Name)));

            return PageTemplateHarness.RealiseElement(text, new Payload("present"));
        });

        Assert.True(
            failures.Count == 0,
            "a binding to a property that exists was reported as a failure:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// No implicit template means a throw naming the type, not a cast exception or
    /// an empty window. A template that is keyed rather than implicit, or in a
    /// dictionary the fixture never merged, lands here — and the type name is the
    /// only thing that tells the reader which of those it was.
    /// </summary>
    [Fact]
    public void RealisingATypeWithNoImplicitTemplateNamesThatType()
    {
        var message = this.wpf.Run(() => Assert
            .Throws<InvalidOperationException>(
                () => PageTemplateHarness.Realise(new Payload("unbound")))
            .Message);

        Assert.Contains(typeof(Payload).FullName!, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The round trip the page tests are built on: a template goes in, a realised
    /// visual tree comes out, and <c>Descendants</c> finds the element in it
    /// carrying the value the binding transferred. Asserting on the text rather
    /// than on the TextBlock's existence is the point — a template can realise an
    /// element and leave it empty, which is the whole failure mode.
    /// </summary>
    [Fact]
    public void ARealisedTemplateYieldsItsElementWithTheBoundValue()
    {
        var text = this.wpf.Run(() =>
        {
            string? found = null;

            var failures = PageTemplateHarness.RealiseTemplate(
                NameTemplate(),
                new Payload("visible"),
                host => found = PageTemplateHarness.Descendants<TextBlock>(host).Single().Text);

            Assert.True(
                failures.Count == 0,
                string.Join(Environment.NewLine, failures));

            return found;
        });

        Assert.Equal("visible", text);
    }

    // ---- fixtures -----------------------------------------------------------

    /// <summary>A template with one bound TextBlock, built in code so the test
    /// needs no compiled XAML of its own.</summary>
    private static DataTemplate NameTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Payload.Name)));

        return new DataTemplate(typeof(Payload)) { VisualTree = text };
    }

    /// <summary>Something to bind against. Deliberately not a domain type: what is
    /// under test is the harness, not anything it was pointed at.</summary>
    public sealed record Payload(string Name);
}
