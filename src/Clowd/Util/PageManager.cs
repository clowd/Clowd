using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LightInject;

namespace Clowd.Util
{
    public class PageManager : IPageManager
    {
        private readonly IServiceFactory _factory;
        private Dictionary<Type, Scope> _cache = new Dictionary<Type, Scope>();
        private readonly object _lock = new object();

        public PageManager(IServiceFactory factory)
        {
            _factory = factory;
        }

        public IScreenCapturePage CreateScreenCapturePage()
        {
            return CreatePage<IScreenCapturePage>();
        }

        public ISettingsPage CreateSettingsPage()
        {
            return CreatePage<ISettingsPage>();
        }

        public IVideoCapturePage CreateVideoCapturePage()
        {
            return CreatePage<IVideoCapturePage>();
        }

        public ILiveDrawPage CreateLiveDrawPage()
        {
            return CreatePage<ILiveDrawPage>();
        }

        protected T CreatePage<T>() where T : IPage
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(typeof(T), out Scope existing))
                {
                    var page = _factory.GetInstance<T>();
                    return page;
                }
                else
                {
                    var scope = _factory.BeginScope();
                    try
                    {
                        var page = _factory.GetInstance<T>();
                        page.Closed += (s, e) =>
                        {
                            lock (_lock)
                            {
                                _cache.Remove(typeof(T));
                                scope.Dispose();
                            }
                        };
                        _cache[typeof(T)] = scope;
                        return page;
                    }
                    catch
                    {
                        scope.Dispose();
                        throw;
                    }
                }
            }
        }
    }
}
