using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// While an [AvaloniaFact] runs, its <see cref="HeadlessApp"/> and locator scope are the whole
    /// process's, so a plain test running alongside sees an application
    /// (<see cref="SelectedItemViewModelTests.NeedsNoAvaloniaApplication"/> fails) or loses the
    /// asset loader it binds (every <see cref="BackgroundTileTests"/> sheet reads as absent),
    /// depending on how xunit schedules classes. Every headless class must sit in
    /// <see cref="HeadlessApp.Collection"/>, which xunit runs on its own.
    /// </summary>
    public class HeadlessAppTests
    {
        [Fact]
        public void Every_headless_test_class_runs_apart_from_the_plain_tests()
        {
            var definition = typeof(HeadlessAppCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
            Assert.NotNull(definition);
            Assert.Equal(HeadlessApp.Collection, definition.Name);
            Assert.True(definition.DisableParallelization);

            var headless = typeof(HeadlessAppTests).Assembly.GetTypes()
                .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes()
                    .Any(a => a.GetType().Namespace == "Avalonia.Headless.XUnit")))
                .ToList();
            Assert.NotEmpty(headless);
            foreach (var type in headless)
                Assert.True(CollectionOf(type) == HeadlessApp.Collection,
                    $"{type.Name} must be in [Collection(HeadlessApp.Collection)], not '{CollectionOf(type)}'");
        }

        private static string CollectionOf(Type type) =>
            type.GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(CollectionAttribute))
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
                .FirstOrDefault();
    }
}
