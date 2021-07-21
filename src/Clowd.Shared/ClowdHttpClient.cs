using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Clowd
{
    public class ClowdHttpClient : HttpClient
    {
        const string USER_AGENT = "Mozilla/5.0 (compatible; Clowd/1.0)";
        const string JSON_CONTENT_TYPE = "application/json";

        public ClowdHttpClient()
        {
            DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
        }

        public async Task<T> GetJsonAsync<T>(Uri uri)
        {
            HttpResponseMessage response = await GetAsync(uri);
            string content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(content);

            return JsonConvert.DeserializeObject<T>(content);
        }

        public async Task<TRESP> PostNothingAsync<TRESP>(Uri uri, bool throwOnErrorCode = true)
        {
            var response = await PostAsync(uri, new StringContent(""));
            string content = await response.Content.ReadAsStringAsync();
            if (throwOnErrorCode && !response.IsSuccessStatusCode)
                throw new HttpRequestException(content);

            return JsonConvert.DeserializeObject<TRESP>(content);
        }

        public async Task<TRESP> PostJsonAsync<TREQ, TRESP>(Uri uri, TREQ request, bool throwOnErrorCode = true)
        {
            var json = JsonConvert.SerializeObject(request);
            using (var jsoncontent = new StringContent(json, Encoding.UTF8, JSON_CONTENT_TYPE))
            {
                var response = await PostAsync(uri, jsoncontent);
                string content = await response.Content.ReadAsStringAsync();
                if (throwOnErrorCode && !response.IsSuccessStatusCode)
                    throw new HttpRequestException(content);

                return JsonConvert.DeserializeObject<TRESP>(content);
            }
        }

        public async Task GetFileAsync(Uri uri, string localFilePath)
        {
            var response = await GetAsync(uri);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Error: status code " + response.StatusCode);

            using (var fs = new FileStream(localFilePath, FileMode.CreateNew))
                await response.Content.CopyToAsync(fs);
        }
    }
}
