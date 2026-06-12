using Avalonia;
using Avalonia.Headless;
using Clowd.Drawing.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Clowd.Drawing.Tests
{
    public class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Application>()
                      .UseSkia()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
