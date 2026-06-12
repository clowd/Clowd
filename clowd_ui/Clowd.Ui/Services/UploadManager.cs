using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clowd.UI.Helpers;

namespace Clowd
{
    // stub: no upload providers ship in this build (§2.11). UploadSession shows a notice dialog,
    // GetAvailableProviders always returns an empty sequence (the editor's right-click provider
    // menu stays empty as a result).
    public static class UploadManager
    {
        public static Task UploadSession(SessionInfo session, IUploadProvider provider = null)
        {
            return NiceDialog.ShowNoticeAsync(
                null,
                NiceDialogIcon.Information,
                "Upload providers are not available in this build.",
                "Upload unavailable");
        }

        public static IEnumerable<IUploadProvider> GetAvailableProviders(SupportedUploadType type)
        {
            return Enumerable.Empty<IUploadProvider>();
        }
    }
}
