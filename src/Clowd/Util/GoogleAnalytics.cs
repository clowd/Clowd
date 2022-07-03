using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Clowd.PlatformUtil;
using NLog;

namespace Clowd.Util
{
    public class GoogleAnalytics
    {
        private readonly string _uaKey;
        public string ClientId { get; }

        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

        public GoogleAnalytics(string clientId, string uaKey)
        {
            _uaKey = uaKey;
            ClientId = clientId;
        }

        protected virtual Dictionary<string, string> GetBaseProperties(string type)
        {
            string towh(ScreenRect r) => $"{r.Width}x{r.Height}";

            // https://developers.google.com/analytics/devguides/collection/protocol/v1/parameters
            Dictionary<string, string> query = new()
            {
                { "v", "1" },
                { "tid", _uaKey },
                { "ds", "win-x64" },
                { "cid", ClientId },
                { "sr", towh(Platform.Current.VirtualScreen.Bounds) },
                { "vp", towh(Platform.Current.PrimaryScreen.Bounds) },
                { "an", "Clowd" },
                { "av", SquirrelUtil.CurrentVersion },
                { "t", type },
            };

            return query;
        }

        protected virtual async void SendHit(Dictionary<string, string> props)
        {
            if (String.IsNullOrWhiteSpace(_uaKey) || String.IsNullOrWhiteSpace(ClientId))
                return;

            // https://developers.google.com/analytics/devguides/collection/protocol/v1/devguide#required
            using var http = new ClowdHttpClient();
            var query = String.Join("&", props.Select(kvp => $"{kvp.Key}={HttpUtility.UrlEncode(kvp.Value)}"));
            var url = "https://www.google-analytics.com/collect";

            try
            {
                var r = await http.PostAsync(url, new StringContent(query), CancellationToken.None).ConfigureAwait(false);
                r.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                _log.Error(e, "Failed to log metric to UA");
            }
        }

        public void Event(string category, string action, bool interactive = false)
        {
            var query = GetBaseProperties("event");
            query.Add("ea", category);
            query.Add("ec", action);

            if (!interactive)
                query.Add("ni", "1");

            SendHit(query);
        }

        public void ScreenView(string screenName)
        {
            // var query = GetBaseProperties("screenview");
            // query.Add("cd", screenName);
            var query = GetBaseProperties("pageview");
            query.Add("dh", "app");
            query.Add("dp", "/" + screenName);
            SendHit(query);
        }

        public void Timing(string category, string variableName, int timeMs)
        {
            var query = GetBaseProperties("timing");
            query.Add("utc", category);
            query.Add("utv", variableName);
            query.Add("utt", timeMs.ToString());
            SendHit(query);
        }

        public void Exception(string description, bool fatal)
        {
            var query = GetBaseProperties("exception");
            query.Add("exd", description);
            query.Add("exf", fatal ? "1" : "0");
            SendHit(query);
        }

        public void StartSession()
        {
            var query = GetBaseProperties("event");
            query.Add("ea", "lifecycle");
            query.Add("ec", "processstart");
            query.Add("sc", "start");
            query.Add("ni", "1");
            SendHit(query);
        }

        public void EndSession()
        {
            var query = GetBaseProperties("event");
            query.Add("ea", "lifecycle");
            query.Add("ec", "processend");
            query.Add("sc", "end");
            query.Add("ni", "1");
            SendHit(query);
        }
    }
}
