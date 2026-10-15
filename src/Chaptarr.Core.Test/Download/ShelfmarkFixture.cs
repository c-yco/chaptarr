using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Shelfmark;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Shelfmark;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using ShelfmarkClient = NzbDrone.Core.Download.Clients.Shelfmark.Shelfmark;
using ShelfmarkIndexer = NzbDrone.Core.Indexers.Shelfmark.Shelfmark;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ShelfmarkFixture
    {
        public class StubProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);
        }

        private sealed class EventRecorder : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent message)
                where TEvent : class, IEvent => Events.Add(message);
        }

        private string _folder;
        private IDiskProvider _disk;
        private IHttpClient _http;
        private IAppFolderInfo _appFolders;
        private ShelfmarkDownloadSettings _settings;
        private List<HttpRequest> _requests;
        private JObject _status;
        private JObject _search;
        private HttpStatusCode _responseCode;
        private string _fileContentType;
        private byte[] _fileBytes;
        private bool _failTransfer;
        private List<string> _indexerStatusCalls;

        private static T Stub<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var stub = DispatchProxy.Create<T, StubProxy>();
            ((StubProxy)(object)stub).Handler = handler;
            return stub;
        }

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shelfmark-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _settings = new ShelfmarkDownloadSettings
            {
                BaseUrl = "https://shelfmark.example/prefix",
                ApiKey = "test-key",
                DownloadFolder = _folder
            };
            _requests = new List<HttpRequest>();
            _status = new JObject();
            _search = JObject.Parse("{\"releases\":[]}");
            _responseCode = HttpStatusCode.OK;
            _fileContentType = "application/epub+zip";
            _fileBytes = Encoding.UTF8.GetBytes("test book content");
            _failTransfer = false;
            _indexerStatusCalls = new List<string>();
            _appFolders = Stub<IAppFolderInfo>((method, args) => method.Name == "get_AppDataFolder" ? _folder : throw new AssertionException(method.Name));
            _disk = Stub<IDiskProvider>((method, args) =>
            {
                var path = (string)args[0];
                switch (method.Name)
                {
                    case nameof(IDiskProvider.FolderExists): return Directory.Exists(path);
                    case nameof(IDiskProvider.FileExists): return File.Exists(path);
                    case nameof(IDiskProvider.FolderWritable): return true;
                    case nameof(IDiskProvider.GetFiles): return Directory.GetFiles(path);
                    case nameof(IDiskProvider.ReadAllText): return File.ReadAllText(path);
                    case nameof(IDiskProvider.GetFileSize): return new FileInfo(path).Length;
                    case nameof(IDiskProvider.OpenWriteStream): return new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
                    case nameof(IDiskProvider.CreateFolder): Directory.CreateDirectory(path); return null;
                    case nameof(IDiskProvider.WriteAllText): File.WriteAllText(path, (string)args[1]); return null;
                    case nameof(IDiskProvider.MoveFile): File.Move(path, (string)args[1], (bool)args[2]); return null;
                    case nameof(IDiskProvider.DeleteFile): File.Delete(path); return null;
                    case nameof(IDiskProvider.DeleteFolder): Directory.Delete(path, (bool)args[1]); return null;
                    default: throw new AssertionException(method.Name);
                }
            });
            _http = Stub<IHttpClient>((method, args) =>
            {
                var response = Respond((HttpRequest)args[0]);
                return method.Name.EndsWith("Async", StringComparison.Ordinal) ? Task.FromResult(response) : response;
            });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private HttpResponse Respond(HttpRequest request)
        {
            _requests.Add(request);
            var headers = new HttpHeader();
            var uri = new Uri(request.Url.FullUri);
            if (uri.AbsolutePath.EndsWith("/localdownload", StringComparison.Ordinal))
            {
                request.ResponseStream.Write(_fileBytes);
                if (_failTransfer)
                {
                    throw new IOException("Interrupted transfer");
                }

                headers.ContentType = _fileContentType;
                return new HttpResponse(request, headers, Array.Empty<byte>(), _responseCode);
            }

            headers.ContentType = "application/json";
            var body = uri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal) ? _status :
                uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) ? _search : JObject.Parse("{\"status\":\"queued\"}");
            return new HttpResponse(request, headers, body.ToString(), _responseCode);
        }

        private ShelfmarkClient Client()
        {
            return new ShelfmarkClient(_http, _appFolders, null, _disk, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition { Id = 42, Name = "Shelfmark", Settings = _settings }
            };
        }

        private ShelfmarkIndexer Indexer()
        {
            var http = Stub<IIndexerHttpClient>((method, args) => Task.FromResult(Respond((HttpRequest)args[0])));
            var factory = Stub<IIndexerHttpClientFactory>((method, args) =>
            {
                Assert.That(args[0], Is.EqualTo(7));
                return http;
            });
            var statuses = Stub<IIndexerStatusService>((method, args) =>
            {
                _indexerStatusCalls.Add(method.Name);
                return null;
            });
            return new ShelfmarkIndexer(factory, statuses, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new IndexerDefinition
                {
                    Id = 99,
                    Name = "Shelfmark",
                    Settings = new ShelfmarkIndexerSettings { BaseUrl = _settings.BaseUrl, ApiKey = _settings.ApiKey },
                    ProxyId = 7
                }
            };
        }

        private RemoteBook Book()
        {
            var release = JObject.Parse("{\"source\":\"direct_download\",\"source_id\":\"abc/123\",\"title\":\"Dune\",\"format\":\"epub\",\"extra\":{\"author\":\"Frank Herbert\"}}");
            return new RemoteBook
            {
                Books = new List<Book> { new Book { Id = 1, MediaType = BookMediaType.Ebook } },
                Author = new Author { Id = 2, Name = "Frank Herbert" },
                Release = new ReleaseInfo
                {
                    Title = "Frank Herbert - Dune.epub",
                    Size = 200,
                    DownloadProtocol = DownloadProtocol.Direct,
                    DownloadUrl = ShelfmarkApi.ReleaseUrl(_settings, release)
                }
            };
        }

        private void Status(string state)
        {
            _status = new JObject
            {
                [state] = new JObject
                {
                    ["abc/123"] = new JObject { ["status"] = state, ["progress"] = 50, ["status_message"] = "Shelfmark status" },
                    ["unrelated"] = new JObject { ["status"] = state }
                }
            };
        }

        [Test]
        public async Task should_queue_release_with_key_and_persist_tracking_across_client_recreation()
        {
            var id = await Client().Download(Book(), null);
            Status("downloading");
            var item = Client().GetItems().Single();

            Assert.That(item.DownloadId, Is.EqualTo(id));
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Downloading));
            Assert.That(item.RemainingSize, Is.EqualTo(100));
            Assert.That(_requests[0].Method.Method, Is.EqualTo("POST"));
            Assert.That(JObject.Parse(Encoding.UTF8.GetString(_requests[0].ContentData))["extra"]?["author"]?.ToString(), Is.EqualTo("Frank Herbert"));
            Assert.That(_requests.All(request => request.Headers["X-Api-Key"].ToString() == "test-key"), Is.True);
            Assert.That(_requests.All(request => !request.AllowAutoRedirect && !request.StoreRequestCookie && !request.StoreResponseCookie), Is.True);
            Assert.That(_requests.Any(request => new Uri(request.Url.FullUri).AbsolutePath.EndsWith("/localdownload", StringComparison.Ordinal)), Is.False);
        }

        [TestCase("queued", DownloadItemStatus.Queued)]
        [TestCase("resolving", DownloadItemStatus.Downloading)]
        [TestCase("locating", DownloadItemStatus.Downloading)]
        [TestCase("error", DownloadItemStatus.Failed)]
        [TestCase("cancelled", DownloadItemStatus.Failed)]
        public async Task should_map_shelfmark_states(string state, DownloadItemStatus expected)
        {
            await Client().Download(Book(), null);
            Status(state);
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(expected));
            Assert.That(item.Message, Is.EqualTo("Shelfmark status"));
            Assert.That(_requests.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task should_retrieve_only_completed_books_and_expose_authoritative_local_files()
        {
            await Client().Download(Book(), null);
            Status("complete");
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(item.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
            Assert.That(File.ReadAllBytes(item.FilePaths.Single()), Is.EqualTo(_fileBytes));
            Assert.That(Directory.GetFiles(item.OutputPath.FullPath, "*.partial"), Is.Empty);
            Assert.That(_requests.Last().Url.FullUri, Does.Contain("id=abc%2F123"));

            _status = new JObject();
            var requestCount = _requests.Count;
            var restored = Client().GetItems().Single();
            Assert.That(restored.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(_requests.Count, Is.EqualTo(requestCount + 1), "Completed files must not be fetched again");

            var events = new EventRecorder();
            var history = Stub<IHistoryService>((method, args) => new EntityHistory { EventType = EntityHistoryEventType.Grabbed });
            var importItem = Stub<IProvideImportItemService>((method, args) => args[0]);
            var subject = new CompletedDownloadService(events, history, importItem, null, null, null, null,
                LogManager.GetCurrentClassLogger(), NoopDownloadClientFileSnapshotService.Instance);
            var tracked = new TrackedDownload { DownloadItem = restored, RemoteBook = Book() };
            subject.Check(tracked);
            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Assert.That(tracked.ImportItem.FilePaths.Single(), Is.EqualTo(item.FilePaths.Single()));
        }

        [TestCase("text/html")]
        [TestCase("application/json")]
        public async Task should_not_import_web_pages_or_error_payloads(string contentType)
        {
            await Client().Download(Book(), null);
            Status("complete");
            _fileContentType = contentType;
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Warning));
            Assert.That(item.FilePaths, Is.Null);
            Assert.That(Directory.GetFiles(item.OutputPath.FullPath, "*.epub"), Is.Empty);
        }

        [Test]
        public async Task should_retry_interrupted_transfer_without_exposing_partial_file()
        {
            await Client().Download(Book(), null);
            Status("complete");
            _failTransfer = true;
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Warning));
            Assert.That(item.Message, Does.Contain("Interrupted transfer"));
            Assert.That(Directory.GetFiles(item.OutputPath.FullPath, "*.epub"), Is.Empty);

            _failTransfer = false;
            item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(File.ReadAllBytes(item.FilePaths.Single()), Is.EqualTo(_fileBytes));
        }

        [Test]
        public async Task should_not_import_an_empty_file()
        {
            await Client().Download(Book(), null);
            Status("complete");
            _fileBytes = Array.Empty<byte>();
            Assert.That(Client().GetItems().Single().Status, Is.EqualTo(DownloadItemStatus.Warning));
        }

        [Test]
        public async Task should_warn_if_shelfmark_loses_the_queue_entry()
        {
            await Client().Download(Book(), null);
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Warning));
            Assert.That(item.Message, Does.Contain("missing from Shelfmark"));
        }

        [Test]
        public async Task should_skip_retrieval_and_library_import_when_shelfmark_delivers_to_grimmory()
        {
            _settings.SkipLibraryImport = true;
            _settings.DownloadFolder = null;
            await Client().Download(Book(), null);
            Status("complete");
            var item = Client().GetItems().Single();
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(item.SkipImport, Is.True);
            Assert.That(item.OutputPath.IsEmpty, Is.True);
            Assert.That(item.FilePaths, Is.Null);
            Assert.That(_requests.Count, Is.EqualTo(2));

            var events = new EventRecorder();
            var subject = new CompletedDownloadService(events, null, null, null, null, null, null,
                LogManager.GetCurrentClassLogger(), NoopDownloadClientFileSnapshotService.Instance);
            var tracked = new TrackedDownload { DownloadItem = item, RemoteBook = Book() };
            subject.Check(tracked);
            subject.Check(tracked);
            subject.Import(tracked);

            Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Ignored));
            var ignored = events.Events.OfType<DownloadIgnoredEvent>().Single();
            Assert.That(ignored.BookIds, Is.EqualTo(new[] { 1 }));
            Assert.That(ignored.Message, Does.Contain("import skipped"));
            Assert.That(events.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
        }

        [Test]
        public async Task should_capture_skip_option_at_queue_time()
        {
            _settings.SkipLibraryImport = true;
            await Client().Download(Book(), null);
            _settings.SkipLibraryImport = false;
            Status("complete");
            Assert.That(Client().GetItems().Single().SkipImport, Is.True);
            Assert.That(_requests.Count, Is.EqualTo(2));
        }

        [Test]
        public async Task should_keep_import_mode_and_staging_folder_for_existing_downloads()
        {
            await Client().Download(Book(), null);
            _settings.SkipLibraryImport = true;
            _settings.DownloadFolder = null;
            Status("complete");
            var item = Client().GetItems().Single();
            Assert.That(item.SkipImport, Is.False);
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(File.Exists(item.FilePaths.Single()), Is.True);
            Assert.That(item.OutputPath.FullPath, Does.StartWith(_folder));
        }

        [Test]
        public async Task should_remove_external_delivery_tracking_without_calling_shelfmark_or_touching_a_library()
        {
            _settings.SkipLibraryImport = true;
            _settings.DownloadFolder = null;
            var client = Client();
            await client.Download(Book(), null);
            Status("complete");
            var item = client.GetItems().Single();
            var requestCount = _requests.Count;
            client.RemoveItem(item, true);
            Assert.That(client.GetItems(), Is.Empty);
            Assert.That(_requests.Count, Is.EqualTo(requestCount));
        }

        [Test]
        public async Task should_keep_identical_source_ids_isolated_between_clients()
        {
            var firstId = await Client().Download(Book(), null);
            var second = Client();
            second.Definition.Id = 43;
            var secondId = await second.Download(Book(), null);
            Status("queued");
            Assert.That(firstId, Is.Not.EqualTo(secondId));
            Assert.That(Client().GetItems().Single().DownloadId, Is.EqualTo(firstId));
            Assert.That(second.GetItems().Single().DownloadId, Is.EqualTo(secondId));
        }

        [Test]
        public async Task should_remove_only_local_tracking_and_staged_data_for_completed_downloads()
        {
            await Client().Download(Book(), null);
            Status("complete");
            var client = Client();
            var item = client.GetItems().Single();
            client.RemoveItem(item, true);
            Assert.That(client.GetItems(), Is.Empty);
            Assert.That(Directory.Exists(item.OutputPath.FullPath), Is.False);
            Assert.That(_requests.All(request => request.Method.Method != "DELETE"), Is.True);
        }

        [Test]
        public async Task should_preserve_staged_files_when_removing_without_deleting_data()
        {
            await Client().Download(Book(), null);
            Status("complete");
            var client = Client();
            var item = client.GetItems().Single();
            client.RemoveItem(item, false);
            Assert.That(File.Exists(item.FilePaths.Single()), Is.True);
            Assert.That(client.GetItems(), Is.Empty);
        }

        [Test]
        public async Task should_cancel_active_download_when_removed()
        {
            await Client().Download(Book(), null);
            Status("downloading");
            var client = Client();
            client.RemoveItem(client.GetItems().Single(), true);
            Assert.That(_requests.Last().Method.Method, Is.EqualTo("DELETE"));
            Assert.That(_requests.Last().Url.FullUri, Does.Contain("abc%2F123"));
            Assert.That(client.GetItems(), Is.Empty);
        }

        [Test]
        public void should_reject_invalid_download_ids_before_touching_disk()
        {
            Assert.Throws<DownloadClientException>(() => Client().RemoveItem(new DownloadClientItem { DownloadId = "..\\..\\library" }, true));
        }

        [Test]
        public async Task should_reject_duplicate_queue_submissions()
        {
            await Client().Download(Book(), null);
            Assert.ThrowsAsync<DownloadClientRejectedReleaseException>(async () => await Client().Download(Book(), null));
            Assert.That(_requests.Count, Is.EqualTo(1));
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void should_surface_authentication_failures(HttpStatusCode code)
        {
            _responseCode = code;
            Assert.ThrowsAsync<DownloadClientAuthenticationException>(async () => await Client().Download(Book(), null));
            Assert.That(Client().GetItems(), Is.Empty);
        }

        [Test]
        public void should_require_matching_shelfmark_instances()
        {
            var book = Book();
            _settings.BaseUrl = "https://other.example";
            Assert.ThrowsAsync<DownloadClientException>(async () => await Client().Download(book, null));
            Assert.That(_requests, Is.Empty);
        }

        [TestCase("torrent")]
        [TestCase("nzb")]
        public void should_reject_non_http_releases_even_from_direct_source(string protocol)
        {
            var book = Book();
            var payload = ShelfmarkApi.ReadRelease(_settings, book.Release.DownloadUrl);
            payload["protocol"] = protocol;
            book.Release.DownloadUrl = ShelfmarkApi.ReleaseUrl(_settings, payload);
            Assert.ThrowsAsync<DownloadClientException>(async () => await Client().Download(book, null));
            Assert.That(_requests, Is.Empty);
        }

        [TestCase(HttpStatusCode.Redirect)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void should_reject_redirects_and_http_failures_without_persisting_success(HttpStatusCode code)
        {
            _responseCode = code;
            Assert.ThrowsAsync<DownloadClientException>(async () => await Client().Download(Book(), null));
            Assert.That(Client().GetItems(), Is.Empty);
        }

        [Test]
        public async Task should_stage_supported_single_file_audiobooks()
        {
            var book = Book();
            var payload = ShelfmarkApi.ReadRelease(_settings, book.Release.DownloadUrl);
            payload["format"] = "m4b";
            book.Release.Title = "Frank Herbert - Dune.m4b";
            book.Release.DownloadUrl = ShelfmarkApi.ReleaseUrl(_settings, payload);
            book.Books.Single().MediaType = BookMediaType.Audiobook;
            await Client().Download(book, null);
            Status("complete");
            var item = Client().GetItems().Single();
            Assert.That(item.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(item.Status, Is.EqualTo(DownloadItemStatus.Completed));
            Assert.That(Path.GetExtension(item.FilePaths.Single()), Is.EqualTo(".m4b"));
        }

        [Test]
        public void should_validate_folder_only_when_importing_and_not_apply_usenet_delay_to_direct_downloads()
        {
            _settings.DownloadFolder = null;
            Assert.That(_settings.Validate().IsValid, Is.False);
            _settings.SkipLibraryImport = true;
            Assert.That(_settings.Validate().IsValid, Is.True);
            Assert.That(new DelayProfile { UsenetDelay = 60, TorrentDelay = 30 }.GetProtocolDelay(DownloadProtocol.Direct), Is.Zero);
        }

        [TestCase(BookMediaType.Ebook, "ebook", "epub")]
        [TestCase(BookMediaType.Audiobook, "audiobook", "m4b")]
        public async Task should_search_only_direct_source_with_media_scoping_and_preserve_release_payload(BookMediaType mediaType, string contentType, string format)
        {
            _search = JObject.Parse("{\"releases\":[" +
                "{\"source\":\"direct_download\",\"source_id\":\"1\",\"title\":\"Dune\",\"format\":\"" + format + "\",\"size_bytes\":1234,\"language\":\"en\",\"extra\":{\"author\":\"Frank Herbert\"}}," +
                "{\"source\":\"prowlarr\",\"source_id\":\"2\",\"title\":\"Other\",\"format\":\"epub\"}," +
                "{\"source\":\"direct_download\",\"source_id\":\"3\",\"title\":\"Archive\",\"format\":\"zip\"}]}");
            var indexer = Indexer();
            var releases = await indexer.Fetch(new BookSearchCriteria
            {
                BookTitle = "Dune",
                Author = new Author { Name = "Frank Herbert" },
                Books = new List<Book> { new Book { MediaType = mediaType } }
            });
            var release = releases.Single();
            Assert.That(release.Title, Is.EqualTo("Frank Herbert - Dune." + format));
            Assert.That(release.DownloadProtocol, Is.EqualTo(DownloadProtocol.Direct));
            Assert.That(release.Size, Is.EqualTo(1234));
            Assert.That(release.Languages.Single().Name, Is.EqualTo("English"));
            Assert.That(release.IndexerId, Is.EqualTo(99));
            Assert.That(_requests.Single().Url.FullUri, Does.Contain("content_type=" + contentType));
            Assert.That(_requests.Single().Url.FullUri, Does.Contain("source=direct_download"));
            Assert.That(ShelfmarkApi.ReadRelease(_settings, release.DownloadUrl)["extra"]?["author"]?.ToString(), Is.EqualTo("Frank Herbert"));
            Assert.That(indexer.SupportsRss, Is.False);
            Assert.That(await indexer.FetchRecent(), Is.Empty);
            Assert.That(_indexerStatusCalls, Is.EqualTo(new[] { nameof(IIndexerStatusService.RecordSuccess) }));
        }

        [Test]
        public async Task should_search_both_media_types_for_unscoped_author_searches()
        {
            var releases = await Indexer().Fetch(new AuthorSearchCriteria { Author = new Author { Name = "Frank Herbert" } });
            Assert.That(releases, Is.Empty);
            Assert.That(_requests.Count, Is.EqualTo(2));
            Assert.That(_requests[0].Url.FullUri, Does.Contain("content_type=ebook"));
            Assert.That(_requests[1].Url.FullUri, Does.Contain("content_type=audiobook"));
        }

        [Test]
        public void should_record_indexer_failure_for_malformed_search_responses()
        {
            _search = new JObject();
            Assert.ThrowsAsync<InvalidOperationException>(async () => await Indexer().Fetch(new BookSearchCriteria
            {
                BookTitle = "Dune",
                Author = new Author { Name = "Frank Herbert" }
            }));
            Assert.That(_indexerStatusCalls, Is.EqualTo(new[] { nameof(IIndexerStatusService.RecordFailure) }));
        }

        [Test]
        public void should_record_indexer_authentication_failure_without_a_success_shaped_empty_search()
        {
            _responseCode = HttpStatusCode.Unauthorized;
            Assert.ThrowsAsync<DownloadClientAuthenticationException>(async () => await Indexer().Fetch(new BookSearchCriteria
            {
                BookTitle = "Dune",
                Author = new Author { Name = "Frank Herbert" }
            }));
            Assert.That(_indexerStatusCalls, Is.EqualTo(new[] { nameof(IIndexerStatusService.RecordFailure) }));
        }
    }
}
