using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Shelfmark;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Shelfmark
{
    public class Shelfmark : DownloadClientBase<ShelfmarkDownloadSettings>
    {
        private readonly IHttpClient _httpClient;
        private readonly IAppFolderInfo _appFolderInfo;

        public Shelfmark(IHttpClient httpClient, IAppFolderInfo appFolderInfo, IConfigService configService, IDiskProvider diskProvider,
            IRemotePathMappingService remotePathMappingService, Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger)
        {
            _httpClient = httpClient;
            _appFolderInfo = appFolderInfo;
        }

        public override string Name => "Shelfmark";
        public override DownloadProtocol Protocol => DownloadProtocol.Direct;

        private string JobFolder => Path.Combine(_appFolderInfo.AppDataFolder, "shelfmark", Definition.Id.ToString());

        public override async Task<string> Download(RemoteBook remoteBook, IIndexer indexer)
        {
            var release = ShelfmarkApi.ReadRelease(Settings, remoteBook.Release.DownloadUrl);
            var job = new ShelfmarkJob
            {
                SourceId = (string)release["source_id"],
                Title = remoteBook.Release.Title,
                Format = ((string)release["format"]).ToLowerInvariant(),
                Size = remoteBook.Release.Size,
                MediaType = remoteBook.GetPreferredMediaType(),
                SkipLibraryImport = Settings.SkipLibraryImport,
                DownloadFolder = Settings.SkipLibraryImport ? null : Settings.DownloadFolder
            };
            var id = GetId(job);
            if (_diskProvider.FileExists(GetJobPath(id)))
            {
                throw new DownloadClientRejectedReleaseException(remoteBook.Release, "This release is already tracked by the Shelfmark download client.");
            }

            _diskProvider.CreateFolder(JobFolder);
            var request = ShelfmarkApi.Request(Settings, "releases/download").Build();
            request.Method = HttpMethod.Post;
            request.Headers.ContentType = "application/json";
            request.SetContent(release.ToString());
            var response = ShelfmarkApi.ReadResponse(await _httpClient.ExecuteAsync(request));
            if ((string)response["status"] != "queued")
            {
                throw new DownloadClientException("Shelfmark did not acknowledge the queued download.");
            }

            SaveJob(job);
            return id;
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            if (!_diskProvider.FolderExists(JobFolder))
            {
                return Array.Empty<DownloadClientItem>();
            }

            var jobs = _diskProvider.GetFiles(JobFolder, false).Where(file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(file => Json.Deserialize<ShelfmarkJob>(_diskProvider.ReadAllText(file))).ToList();
            if (jobs.Count == 0)
            {
                return Array.Empty<DownloadClientItem>();
            }

            var status = ShelfmarkApi.ReadResponse(_httpClient.Execute(ShelfmarkApi.Request(Settings, "status").Build()));
            var tasks = status.Properties().SelectMany(group => (group.Value as JObject ??
                throw new DownloadClientException("Invalid Shelfmark status group.")).Properties())
                .ToDictionary(task => task.Name, task => (JObject)task.Value, StringComparer.Ordinal);
            return jobs.Select(job => GetItem(job, tasks.GetValueOrDefault(job.SourceId))).ToList();
        }

        private DownloadClientItem GetItem(ShelfmarkJob job, JObject task)
        {
            var id = GetId(job);
            var outputFolder = job.SkipLibraryImport ? null : GetOutputFolder(job);
            var outputFile = job.SkipLibraryImport ? null : Path.Combine(outputFolder, FileNameBuilder.CleanFileName(job.Title) + "." + job.Format);
            var state = (string)task?["status"];
            var status = job.Transferred ? DownloadItemStatus.Completed : state switch
            {
                "queued" => DownloadItemStatus.Queued,
                "resolving" or "locating" or "downloading" => DownloadItemStatus.Downloading,
                "complete" => job.SkipLibraryImport ? DownloadItemStatus.Completed : DownloadItemStatus.Downloading,
                "error" or "cancelled" => DownloadItemStatus.Failed,
                _ => DownloadItemStatus.Warning
            };
            var message = (string)task?["status_message"];

            if (!job.Transferred && !job.SkipLibraryImport && state == "complete")
            {
                try
                {
                    Transfer(job, outputFile);
                    job.Transferred = true;
                    SaveJob(job);
                    status = DownloadItemStatus.Completed;
                }
                catch (Exception ex) when (ex is IOException || ex is HttpException || ex is DownloadClientException)
                {
                    _logger.Warn(ex, "Unable to retrieve completed Shelfmark download {0}", job.Title);
                    status = DownloadItemStatus.Warning;
                    message = "Unable to retrieve completed book from Shelfmark: " + ex.Message;
                }
            }
            else if (!job.Transferred && task == null)
            {
                message = "Download is missing from Shelfmark. Restore its queue entry or remove and retry this download.";
            }

            return new DownloadClientItem
            {
                DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                DownloadId = id,
                Title = job.Title,
                MediaType = job.MediaType,
                TotalSize = job.Size,
                RemainingSize = status == DownloadItemStatus.Completed ? 0 :
                    (long)(job.Size * (1 - Math.Clamp((double?)task?["progress"] ?? 0, 0, 100) / 100)),
                Status = status,
                Message = message,
                OutputPath = job.SkipLibraryImport ? default : new OsPath(outputFolder),
                SkipImport = job.SkipLibraryImport,
                FilePaths = job.Transferred ? new List<string> { outputFile } : null,
                FileListConfidence = job.Transferred ? DownloadClientFileListConfidence.Authoritative : DownloadClientFileListConfidence.Pending,
                CanMoveFiles = true,
                CanBeRemoved = true
            };
        }

        private void Transfer(ShelfmarkJob job, string outputFile)
        {
            _diskProvider.CreateFolder(Path.GetDirectoryName(outputFile));
            var partialFile = outputFile + ".partial";
            if (_diskProvider.FileExists(partialFile))
            {
                _diskProvider.DeleteFile(partialFile);
            }

            using (var stream = _diskProvider.OpenWriteStream(partialFile))
            {
                var request = ShelfmarkApi.Request(Settings, "localdownload").AddQueryParam("id", job.SourceId).Build();
                request.ResponseStream = stream;
                var response = _httpClient.Execute(request);
                ShelfmarkApi.CheckResponse(response);
                var contentType = response.Headers.ContentType ?? string.Empty;
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DownloadClientException("Shelfmark returned a web page or JSON instead of a book.");
                }
            }

            if (_diskProvider.GetFileSize(partialFile) == 0)
            {
                throw new DownloadClientException("Shelfmark returned an empty book file.");
            }

            _diskProvider.MoveFile(partialFile, outputFile, true);
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            var jobPath = GetJobPath(item.DownloadId);
            var job = Json.Deserialize<ShelfmarkJob>(_diskProvider.ReadAllText(jobPath));
            if (!job.Transferred && item.Status is DownloadItemStatus.Queued or DownloadItemStatus.Downloading)
            {
                var request = ShelfmarkApi.Request(Settings, "download/" + Uri.EscapeDataString(job.SourceId) + "/cancel").Build();
                request.Method = HttpMethod.Delete;
                ShelfmarkApi.ReadResponse(_httpClient.Execute(request));
            }

            var folder = job.SkipLibraryImport ? null : GetOutputFolder(job);
            if (deleteData && folder != null && _diskProvider.FolderExists(folder))
            {
                _diskProvider.DeleteFolder(folder, true);
            }

            _diskProvider.DeleteFile(jobPath);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = Settings.SkipLibraryImport ? new List<OsPath>() : new List<OsPath> { new OsPath(Settings.DownloadFolder) }
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            ShelfmarkApi.ReadResponse(_httpClient.Execute(ShelfmarkApi.Request(Settings, "status").Build()));
            var folderFailure = Settings.SkipLibraryImport ? null : TestFolder(Settings.DownloadFolder, nameof(Settings.DownloadFolder));
            if (folderFailure != null)
            {
                failures.Add(folderFailure);
            }
        }

        private string GetId(ShelfmarkJob job)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Definition.Id + ":" + job.SourceId)));
        }

        private static void ValidateId(string id)
        {
            if (id == null || id.Length != 64 || !id.All(Uri.IsHexDigit))
            {
                throw new DownloadClientException("Invalid Shelfmark download ID.");
            }
        }

        private string GetJobPath(string id)
        {
            ValidateId(id);
            return Path.Combine(JobFolder, id + ".json");
        }

        private string GetOutputFolder(ShelfmarkJob job)
        {
            return Path.Combine(job.DownloadFolder, "shelfmark-" + Definition.Id + "-" + GetId(job));
        }

        private void SaveJob(ShelfmarkJob job)
        {
            var path = GetJobPath(GetId(job));
            _diskProvider.WriteAllText(path + ".tmp", job.ToJson());
            _diskProvider.MoveFile(path + ".tmp", path, true);
        }
    }
}
