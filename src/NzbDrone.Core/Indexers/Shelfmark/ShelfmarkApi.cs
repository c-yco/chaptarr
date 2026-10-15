using System;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Indexers.Shelfmark
{
    public static class ShelfmarkApi
    {
        public static HttpRequestBuilder Request(ShelfmarkSettings settings, string resource)
        {
            var builder = new HttpRequestBuilder(settings.BaseUrl.TrimEnd('/'))
            {
                ResourceUrl = "api/" + resource,
                AllowAutoRedirect = false,
                SuppressHttpError = true,
                LogResponseContent = false
            };

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                builder.Headers.Set("X-Api-Key", settings.ApiKey);
            }

            builder.PostProcess = request =>
            {
                request.StoreRequestCookie = false;
                request.StoreResponseCookie = false;
                request.RequestTimeout = TimeSpan.FromMinutes(5);
            };
            return builder;
        }

        public static void CheckResponse(HttpResponse response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new DownloadClientAuthenticationException("Shelfmark authentication failed. Check the admin API key and download permissions.");
            }

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                throw new DownloadClientException("Shelfmark returned HTTP {0}. Check the Shelfmark logs and base URL.", (int)response.StatusCode);
            }
        }

        public static JObject ReadResponse(HttpResponse response)
        {
            CheckResponse(response);
            var result = JObject.Parse(response.Content);
            if (result["error"]?.Type == JTokenType.String)
            {
                throw new DownloadClientException("Shelfmark: {0}", result["error"].ToString());
            }

            return result;
        }

        // Keep the release payload in the grab URL so interactive searches and pending
        // releases survive serialization without an in-memory search-result cache.
        public static string ReleaseUrl(ShelfmarkSettings settings, JObject release)
        {
            return Request(settings, "releases/download").Build().Url.FullUri + "#" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(release.ToString(Formatting.None)));
        }

        public static JObject ReadRelease(ShelfmarkSettings settings, string link)
        {
            var expected = Request(settings, "releases/download").Build().Url.FullUri;
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.GetLeftPart(UriPartial.Path), expected, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.Query) || uri.Fragment.Length < 2)
            {
                throw new DownloadClientException("The Shelfmark indexer and download client must use the same base URL.");
            }

            JObject release;
            try
            {
                release = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(uri.Fragment.Substring(1))));
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException)
            {
                throw new DownloadClientException("Invalid Shelfmark release payload", ex);
            }

            if ((string)release["source"] != "direct_download" ||
                string.IsNullOrWhiteSpace((string)release["source_id"]) ||
                ((string)release["protocol"] is string protocol && protocol != "http") ||
                !MediaFileExtensions.AllExtensions.Contains("." + ((string)release["format"])?.ToLowerInvariant()))
            {
                throw new DownloadClientException("Shelfmark release must be a direct download in a supported book format.");
            }

            return release;
        }
    }
}
