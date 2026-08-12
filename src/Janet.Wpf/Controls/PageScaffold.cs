using System.Windows.Controls;

namespace Janet.Wpf.Controls;

/// <summary>
/// The shared page sandwich: a page shows its view-model's
/// <c>EmptyState</c> until <c>HasContent</c>, then its body. One
/// implicit-styled ContentControl (PageTemplates.xaml) instead of a
/// hand-copied Grid-plus-gates pair per page template.
/// </summary>
public sealed class PageScaffold : ContentControl;
