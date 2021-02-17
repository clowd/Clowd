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

        public PageManager(IServiceFactory factory)
        {
            _factory = factory;
        }

        public IScreenCapturePage CreateScreenCapturePage()
        {
            return CreatePage<IScreenCapturePage>();
        }

        public IVideoCapturePage CreateVideoCapturePage()
        {
            return CreatePage<IVideoCapturePage>();
        }

        protected T CreatePage<T>() where T : IPage
        {
            var scope = _factory.BeginScope();
            try
            {
                var page = _factory.GetInstance<T>();
                page.Closed += (s, e) => scope.Dispose();
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
