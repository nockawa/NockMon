# NockMon.Sdk

Write widget plugins for **NockMon** (`dotnet tool install -g NockMon`). Full guide and API reference:
[Plugin development](https://github.com/nockawa/NockMon/blob/main/docs/plugins.md).

## A plugin in four steps

1. A class library:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
       <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
       <PackageId>Acme.NockMon.Widgets</PackageId>
       <Version>1.0.0</Version>
       <Title>Acme widgets</Title>
       <Authors>Acme</Authors>
       <Description>What the plugin shows.</Description>
     </PropertyGroup>
     <ItemGroup>
       <PackageReference Include="NockMon.Sdk" Version="1.0.0" PrivateAssets="all" />
     </ItemGroup>
   </Project>
   ```

2. One `IWidgetPlugin` per widget kind (public, parameterless constructor):

   ```csharp
   public sealed class HelloPlugin : IWidgetPlugin
   {
       public string Id => "hello";                 // stored in layouts: never change it
       public string DisplayName => "Hello";
       public string Summary => "Says hello.";      // tooltips
       public string Details => "**Bold**, `code`, - bullets."; // editor and pinned help
       public WidgetSize DefaultSize => new(3, 2);
       public WidgetSize MinSize => new(2, 1);
       public IPluginWidget CreateWidget(IWidgetContext context) => new HelloWidget();
   }

   sealed class HelloWidget : IPluginWidget
   {
       private readonly TextBlock _text = new();
       public UIElement View => _text;
       public void Refresh() => _text.Text = DateTime.Now.ToLongTimeString(); // UI thread, every sample
       public void Dispose() { }
   }
   ```

3. **Develop**: build, then in NockMon open **Plugins › Development** and add the build output folder (or start
   `nockmon --plugin <output folder>`). The folder is loaded from a copy: rebuild any time, restart the app to reload.

4. **Publish**: `dotnet pack` produces the plugin package (package type `NockMonPlugin`). Try it first by adding the
   pack output folder as a source in **Plugins › Browse › Sources**, then push it to nuget.org: it appears in every
   user's Browse tab.

## Rules of the road

- **UI in code**, no XAML files: compiled XAML resources of a dynamically loaded library do not work in this unpackaged
  app. Use theme resources (`Application.Current.Resources["TextFillColorSecondaryBrush"]`) to follow light/dark.
- **`Refresh` is called on the UI thread at the sample rate** (0.5 s by default): no I/O there. Do network and disk work
  on background tasks; marshal with `IWidgetContext.Dispatcher` or publish immutable state that `Refresh` reads.
- A widget instance lives while it is on the board (created on load, **disposed** when it leaves: layout switch, edit,
  removal). Stop timers and background work in `Dispose`; share expensive pollers between instances.
- **Settings**: `IWidgetContext.Settings` (a `JsonObject`) is persisted in the layout; offer an editor with
  `CreateSettingsEditor`. Files: `IWidgetContext.DataDirectory`.
- The host provides this SDK, the Windows App SDK and the .NET runtime: they are not copied to the output nor packed.
  Other dependencies are, and resolve from the plugin's folder.
- Exceptions thrown by a widget are contained: it shows the error and a Retry link.
- NockMon runs **elevated**: so does your plugin. Users are warned at install; be worthy of it.

## Versioning

The host loads plugins built against the same major and a lower or equal minor of the plugin API
(`PluginApi.Version`, the SDK's major.minor).
