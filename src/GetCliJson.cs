using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FunctionsBuildHelper
{
    public static class GetCliJson
    {
        private const string _cdnRoot = "https://functionscdn.azureedge.net/public";
        private const string _feedUrl = "https://raw.githubusercontent.com/Azure/azure-functions-tooling-feed/master/cli-feed-v3.json";
        private const string _itemTemplateUrl = "https://www.myget.org/F/azure-appservice/api/v2/package/Azure.Functions.Templates/";
        private const string _projTemplateUrl = "https://www.myget.org/F/azure-appservice/api/v2/package/Microsoft.AzureFunctions.ProjectTemplates/";

        // cache this since it's an expensive call
        private static ConcurrentDictionary<string, Task<string>> _templateVersionMap = new ConcurrentDictionary<string, Task<string>>();

        private static HttpClient _client = new HttpClient();
        private static AppVeyorClient _appVeyorClient = AppVeyorClient.Instance;
        private static JsonSerializer _serializer = new JsonSerializer
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        [FunctionName("GetCliJson")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            if (!req.Query.TryGetValue("build", out StringValues values))
            {
                return new BadRequestObjectResult("Missing 'build' value in query string.");
            }

            string build = values.Single();

            var project = await _appVeyorClient.GetProjectByNameAsync("azure-functions-core-tools");
            var jobs = await _appVeyorClient.GetJobInfoAsync(project, build);
            var jobId = jobs[0].jobId;
            var artifacts = await _appVeyorClient.GetArtifactsAsync(jobId);

            // Figure out the build number, which will be in the file name.
            // example -- Azure.Functions.Cli.linux-x64.2.2.27.zip is 2.2.27
            string winX86Zip = artifacts.Select(p => p.fileName).Single(p => p.Contains(".win-x86.") && p.EndsWith(".zip"));

            // Start the zip download, which will get us the template versions.
            Task<string> downloadTask = DownloadAndExtractTemplateVersionAsync(jobId, winX86Zip);

            string version = winX86Zip.Split(".win-x86.")[1].Split(".zip")[0];

            // Loop through the zips
            List<CliEntry> entries = new List<CliEntry>();
            string GetDownloadLink(string file)
            {
                return $"{_cdnRoot}/{version}/{file.Replace("artifacts/", "")}";
            }

            foreach (string file in artifacts.Select(p => p.fileName).Where(p => p.EndsWith(".zip") && !p.Contains(".no-runtime.")))
            {
                var entry = new CliEntry
                {
                    OperatingSystem = GetOperatingSystem(file, onlyMac: true), // only MacOS uses 'OperatingSystem'. Others use 'OS'
                    OS = GetOperatingSystem(file),
                    Architecture = GetArchitecture(file),
                    downloadLink = GetDownloadLink(file),
                    sha2 = await DownloadShaAsync(jobId, file)
                };

                entries.Add(entry);
            }

            // Pull the current feed so we can populate this with the last-known values
            JObject currentFeedJson = null;
            using (var stream = await Helper.HttpClient.GetStreamAsync(_feedUrl))
            {
                using (var reader = new StreamReader(stream))
                {
                    currentFeedJson = JObject.Parse(await reader.ReadToEndAsync());
                }
            }

            var mostRecentRelease = GetRecentRelease(currentFeedJson);

            var feedEntry = mostRecentRelease.ToObject<FeedEntry>();

            // Now overwrite the new values that we've pulled
            feedEntry.cli = GetDownloadLink(winX86Zip);
            feedEntry.sha2 = await DownloadShaAsync(jobId, winX86Zip);
            feedEntry.standaloneCli = entries.ToArray();

            // This is pulling the template version directly from the zip file
            var templateVersion = await downloadTask;
            feedEntry.itemTemplates = _itemTemplateUrl + templateVersion;
            feedEntry.projectTemplates = _projTemplateUrl + templateVersion;

            JObject returnJson = JObject.FromObject(feedEntry, _serializer);

            return new OkObjectResult(returnJson);
        }

        private static Task<string> DownloadAndExtractTemplateVersionAsync(string jobId, string winX86ZipPath)
        {
            return _templateVersionMap.GetOrAdd(jobId, async id =>
            {
                using (Stream stream = await _appVeyorClient.GetArtifactStreamAsync(id, winX86ZipPath))
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    {
                        // Templates are in itemTemplates.2.0.XXXXX.nupkg
                        string startsWith = "itemTemplates.";
                        var templateEntry = archive.Entries.Select(p => p.Name).Single(p => p.StartsWith(startsWith));
                        return templateEntry.Substring(0, templateEntry.LastIndexOf(".")).Replace(startsWith, string.Empty);
                    }
                }
            });
        }

        private static JToken GetRecentRelease(JObject json)
        {
            // Get the "releases"
            var releases = json["releases"] as JObject;

            var comparer = new VersionComparer();
            var release = releases.Children<JProperty>().OrderByDescending(p => p.Name, comparer).First();
            return release.Value;
        }

        private static async Task<string> DownloadShaAsync(string jobId, string file)
        {
            string shaFile = file + ".sha2";
            string sha = null;

            using (var result = await _appVeyorClient.GetArtifactStreamAsync(jobId, shaFile))
            {
                using (var reader = new StreamReader(result))
                {
                    sha = await reader.ReadToEndAsync();
                }
            }

            return sha.Replace("-", string.Empty);
        }

        private static string GetArchitecture(string fileName)
        {
            return fileName.Contains("-x64.") ? "x64" : "x86";
        }

        private static string GetOperatingSystem(string fileName, bool onlyMac = false)
        {
            if (fileName.Contains(".osx-") && onlyMac)
            {
                return "MacOS";
            }

            if (fileName.Contains(".win-") && !onlyMac)
            {
                return "Windows";
            }

            if (fileName.Contains(".linux-") && !onlyMac)
            {
                return "Linux";
            }

            return null;
        }

        private class FeedEntry
        {
            [JsonProperty(PropertyName = "Microsoft.NET.Sdk.Functions")]
            public string MicrosoftNetSdkFunctions { get; set; }
            public string cli { get; set; }
            public string sha2 { get; set; }
            public string nodeVersion { get; set; }
            public string localEntryPoint { get; set; }
            public string itemTemplates { get; set; }
            public string projectTemplates { get; set; }
            public string templateApiZip { get; set; }
            public string FUNCTIONS_EXTENSION_VERSION { get; set; }
            public string requiredRuntime { get; set; }
            public string minimumRuntimeVersion { get; set; }
            public CliEntry[] standaloneCli { get; set; }
        }

        private class CliEntry
        {
            public string OperatingSystem { get; set; }
            public string OS { get; set; }
            public string Architecture { get; set; }
            public string downloadLink { get; set; }
            public string sha2 { get; set; }
        }

        private class VersionComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                // Split them up and compare versions
                var xSplit = x.Split(".");
                var ySplit = y.Split(".");

                var xMax = xSplit.Length;
                var yMax = ySplit.Length;
                var iter = xMax >= yMax ? xMax : yMax;

                for (int i = 0; i < iter; i++)
                {
                    // If we get this far and one is shorter than the other, the longer wins
                    if (xMax != yMax && i == iter - 1)
                    {
                        return xMax < yMax ? -1 : 1;
                    }

                    var xCur = xSplit[i];
                    var yCur = ySplit[i];
                    if (xCur == yCur)
                    {
                        continue;
                    }

                    // Try to convert them to ints
                    if (int.TryParse(xCur, out int xInt) && int.TryParse(yCur, out int yInt))
                    {
                        return xInt - yInt;
                    }

                    // Otherwise just compare as strings (they may have "beta-1", etc)
                    return string.Compare(xSplit[i], ySplit[i]);
                }

                return 0;
            }
        }
    }
}
