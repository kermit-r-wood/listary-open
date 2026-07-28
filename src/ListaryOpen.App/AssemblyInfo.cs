using System.Runtime.CompilerServices;
using System.Windows;

[assembly:InternalsVisibleTo("ListaryOpen.Infrastructure.Tests")]
[assembly:InternalsVisibleTo("ListaryOpen.UiPerformanceBenchmark")]
[assembly: InternalsVisibleTo("ListaryOpen.PreviewHost")]
[assembly:InternalsVisibleTo("ListaryOpen.VisualTests")]
[assembly:InternalsVisibleTo("ListaryOpen.IntegrationTests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
