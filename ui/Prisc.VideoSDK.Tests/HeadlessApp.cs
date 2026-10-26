using Avalonia;
using Avalonia.Headless;
using Clowd.VideoSDK.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// The headless Avalonia app the [AvaloniaFact] tests run in, themed as Clowd is (Semi), so a
    /// control behaves as it does in the app: templated, focusable, with tooltips that open. Only
    /// those tests start it, one app per test (the default PerTest isolation), but while one runs
    /// its app and its <c>AvaloniaLocator</c> scope are the whole process's: a plain [Fact] running
    /// alongside sees an <c>Application.Current</c>, and an asset loader it binds then is gone
    /// with the scope. Hence <see cref="Collection"/>.
    /// </summary>
    public sealed class HeadlessApp : Application
    {
        /// <summary>The xunit collection every [AvaloniaFact] class joins. It is not run in
        /// parallel (<see cref="HeadlessAppCollection"/>), so xunit runs it on its own after every
        /// other collection, and no plain test overlaps a headless one.</summary>
        public const string Collection = "HeadlessApp";

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<HeadlessApp>()
                      .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

        public override void Initialize() => Styles.Add(new Semi.Avalonia.SemiTheme());
    }

    [CollectionDefinition(HeadlessApp.Collection, DisableParallelization = true)]
    public sealed class HeadlessAppCollection
    {
    }
}
