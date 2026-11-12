using Microsoft.UI.Xaml.Markup;

namespace NockMon;

/// <summary>
/// The generated XAML metadata provider throws on a null type, and XAML passes one for a type CsWinRT can't resolve by
/// name: a XAML control subclassed in a plugin (its own load context), during input routing. The exception ended the app;
/// no type is the answer. The explicit implementation takes over the generated public one.
/// </summary>
/// <remarks>
/// Compiled after the second XAML pass only (NockMon.App.csproj): the first pass builds the app without the generated
/// provider, and App doesn't implement the interface yet.
/// </remarks>
public partial class App
{
    IXamlType? IXamlMetadataProvider.GetXamlType(Type? type) => type is null ? null : GetXamlType(type);
}
