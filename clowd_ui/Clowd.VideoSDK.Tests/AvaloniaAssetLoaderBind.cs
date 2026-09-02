using System;
using System.Linq;
using System.Reflection;
using Avalonia;

namespace Clowd.VideoSDK.Tests
{
    /// <summary>
    /// Binds a real <c>IAssetLoader</c> so <c>avares://</c> resolves outside a running app.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two suites need this and neither can afford to guess: <see cref="FileIconAssetTests"/>
    /// reaches for the shipped file-type SVGs and for Inter, and <see cref="BackgroundTileTests"/>
    /// reads the background tiles' loop sheets. Both of those reads degrade silently and by design
    /// when there is no loader (a drawn fallback page in a substitute face; a tile that animates
    /// the wallpaper live), which would let either suite pass while covering none of the asset
    /// path. It lives in its own file because a second copy of the reflection below is exactly the
    /// thing that would rot.
    /// </para>
    /// <para>
    /// Reflection, because Avalonia 12.1's reference assembly hides both
    /// <c>AvaloniaLocator.CurrentMutable</c> and <c>Avalonia.Platform.StandardAssetLoader</c>;
    /// they exist only in the runtime assembly. The alternative is standing up a real
    /// <c>AppBuilder</c>, which drags a windowing platform into a headless test run for the sake
    /// of reading embedded resources. If an Avalonia upgrade moves these, the callers fail loudly
    /// with the reason rather than skipping green, which is deliberate, and the fix is here rather
    /// than in the code being tested.
    /// </para>
    /// <para>
    /// Binding twice is harmless: the locator's registration is a replace, and the loader carries
    /// no state of its own, so two suites racing to bind land on equivalent instances.
    /// </para>
    /// </remarks>
    internal static class AvaloniaAssetLoaderBind
    {
        internal static bool TryBind(out string failure)
        {
            failure = null;
            try
            {
                var runtime = typeof(AvaloniaLocator).Assembly;
                var loaderType = runtime.GetType("Avalonia.Platform.StandardAssetLoader");
                var loaderInterface = runtime.GetType("Avalonia.Platform.IAssetLoader");
                if (loaderType == null || loaderInterface == null)
                {
                    failure = "Avalonia.Platform." +
                        (loaderType == null ? "StandardAssetLoader" : "IAssetLoader") +
                        " is no longer in " + runtime.GetName().Name + ".";
                    return false;
                }

                var currentMutable = typeof(AvaloniaLocator).GetProperty(
                    "CurrentMutable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (currentMutable == null)
                {
                    failure = "AvaloniaLocator.CurrentMutable is gone.";
                    return false;
                }

                // The ctor takes an optional entry assembly, used only to resolve the shorthand
                // "resm:"/relative forms. Every URI here is fully qualified avares://, so null is right.
                object loader = loaderType.GetConstructor(new[] { typeof(Assembly) }) != null
                    ? Activator.CreateInstance(loaderType, new object[] { null })
                    : Activator.CreateInstance(loaderType);

                object locator = currentMutable.GetValue(null);

                var bind = locator.GetType().GetMethods()
                    .First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                    .MakeGenericMethod(loaderInterface);
                object registration = bind.Invoke(locator, null);

                var toConstant = registration.GetType().GetMethods().First(m => m.Name == "ToConstant");
                if (toConstant.IsGenericMethodDefinition)
                    toConstant = toConstant.MakeGenericMethod(loaderInterface);
                toConstant.Invoke(registration, new[] { loader });

                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                return false;
            }
        }
    }
}
