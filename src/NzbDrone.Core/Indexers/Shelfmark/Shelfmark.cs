using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation.Results;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Shelfmark
{
    public class Shelfmark : IndexerBase<ShelfmarkIndexerSettings>
    {
        private readonly IIndexerHttpClientFactory _httpClientFactory;

        public Shelfmark(IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService,
            IConfigService configService, IParsingService parsingService, Logger logger)
            : base(indexerStatusService, configService, parsingService, logger)
        {
            _httpClientFactory = httpClientFactory;
        }

        public override string Name => "Shelfmark";
        public override DownloadProtocol Protocol => DownloadProtocol.Direct;
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;

        private IIndexerHttpClient HttpClient => _httpClientFactory.GetClient(((IndexerDefinition)Definition).ProxyId);

        public override Task<IList<ReleaseInfo>> FetchRecent()
        {
            return Task.FromResult<IList<ReleaseInfo>>(Array.Empty<ReleaseInfo>());
        }

        public override Task<IList<ReleaseInfo>> Fetch(BookSearchCriteria searchCriteria)
        {
            return Search(searchCriteria, searchCriteria.BookTitle);
        }

        public override Task<IList<ReleaseInfo>> Fetch(AuthorSearchCriteria searchCriteria)
        {
            return Search(searchCriteria, null);
        }

        private async Task<IList<ReleaseInfo>> Search(SearchCriteriaBase criteria, string title)
        {
            try
            {
                var releases = await SearchReleases(criteria, title);
                _indexerStatusService.RecordSuccess(Definition.Id);
                return CleanupReleases(releases);
            }
            catch (WebException ex)
            {
                if (ex.Status is WebExceptionStatus.NameResolutionFailure or WebExceptionStatus.ConnectFailure)
                {
                    _indexerStatusService.RecordConnectionFailure(Definition.Id);
                }
                else
                {
                    _indexerStatusService.RecordFailure(Definition.Id);
                }

                throw;
            }
            catch (Exception ex) when (ex is HttpException || ex is DownloadClientException || ex is JsonException || ex is InvalidOperationException)
            {
                _indexerStatusService.RecordFailure(Definition.Id);
                throw;
            }
        }

        private async Task<IList<ReleaseInfo>> SearchReleases(SearchCriteriaBase criteria, string title)
        {
            var releases = new List<ReleaseInfo>();
            var mediaType = SearchMediaTypeHelper.GetRequestedMediaType(criteria);
            var contentTypes = mediaType.HasValue
                ? new[] { mediaType == BookMediaType.Ebook ? "ebook" : "audiobook" }
                : new[] { "ebook", "audiobook" };

            foreach (var contentType in contentTypes)
            {
                var request = ShelfmarkApi.Request(Settings, "releases")
                    .AddQueryParam("source", "direct_download")
                    .AddQueryParam("query", string.Join(" ", new[] { criteria.Author?.Name, title }.Where(v => !string.IsNullOrWhiteSpace(v))))
                    .AddQueryParam("content_type", contentType);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    request.AddQueryParam("title", title);
                }

                if (!string.IsNullOrWhiteSpace(criteria.Author?.Name))
                {
                    request.AddQueryParam("author", criteria.Author.Name);
                }

                var result = ShelfmarkApi.ReadResponse(await HttpClient.ExecuteAsync(request.Build()));
                if (result["errors"] is JArray errors && errors.Count > 0)
                {
                    _logger.Warn("Shelfmark search reported: {0}", string.Join("; ", errors));
                }

                if (result["releases"] is not JArray entries)
                {
                    throw new InvalidOperationException("Shelfmark search response is missing the releases array.");
                }

                foreach (var release in entries.OfType<JObject>())
                {
                    var format = ((string)release["format"])?.ToLowerInvariant();
                    var extensions = contentType == "ebook" ? MediaFileExtensions.TextExtensions : MediaFileExtensions.AudioExtensions;
                    if ((string)release["source"] != "direct_download" ||
                        ((string)release["protocol"] is string protocol && protocol != "http") ||
                        !extensions.Contains("." + format))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace((string)release["source_id"]) || string.IsNullOrWhiteSpace((string)release["title"]))
                    {
                        throw new InvalidOperationException("Shelfmark returned a release without a source ID or title.");
                    }

                    release["content_type"] = contentType;
                    var bookTitle = (string)release["title"];
                    var author = (string)release["author"] ?? (string)release["extra"]?["author"];
                    var languageCode = (string)release["language"];
                    var language = string.IsNullOrWhiteSpace(languageCode) ? null : IsoLanguages.Find(languageCode)?.Language;
                    releases.Add(new ReleaseInfo
                    {
                        Guid = Settings.BaseUrl.TrimEnd('/') + "/release/" + Uri.EscapeDataString((string)release["source_id"]),
                        Title = (string.IsNullOrWhiteSpace(author) ? bookTitle : author + " - " + bookTitle) + "." + format,
                        Author = author,
                        Book = bookTitle,
                        Size = (long?)release["size_bytes"] ?? 0,
                        DownloadUrl = ShelfmarkApi.ReleaseUrl(Settings, release),
                        InfoUrl = (string)release["info_url"],
                        PublishDate = DateTime.UtcNow,
                        Container = format,
                        Languages = language == null ? new List<Language>() : new List<Language> { language }
                    });
                }
            }

            return releases;
        }

        public override HttpRequest GetDownloadRequest(string link)
        {
            throw new NotSupportedException("Shelfmark releases must be sent to the Shelfmark download client.");
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            ShelfmarkApi.ReadResponse(await HttpClient.ExecuteAsync(ShelfmarkApi.Request(Settings, "status").Build()));
        }
    }
}
