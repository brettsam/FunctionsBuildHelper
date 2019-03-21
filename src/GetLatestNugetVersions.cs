using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FunctionsBuildHelper
{
    public static class GetLatestNugetVersions
    {
        private static readonly HttpClient _client = new HttpClient();

        private static readonly string[] packageNames = new[]
        {
            "Microsoft.Azure.WebJobs",
            "Microsoft.Azure.WebJobs.Core",
            "Microsoft.Azure.WebJobs.Extensions",
            "Microsoft.Azure.WebJobs.Extensions.CosmosDB",
            "Microsoft.Azure.WebJobs.Extensions.EventGrid",
            "Microsoft.Azure.WebJobs.Extensions.EventHubs",
            "Microsoft.Azure.WebJobs.Extensions.ServiceBus",
            "Microsoft.Azure.WebJobs.Extensions.Storage",
            "Microsoft.Azure.WebJobs.Host.Storage",
            "Microsoft.Azure.WebJobs.Logging",
            "Microsoft.Azure.WebJobs.Logging.ApplicationInsights"
        };

        private static readonly IReadOnlyDictionary<string, string> nugetSources = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>
            {
                { "App Service Nightly", "https://www.myget.org/F/azure-appservice/api/v3/index.json" },
                { "App Service Staging", "https://www.myget.org/F/azure-appservice-staging/api/v3/index.json" },
                { "Nuget.org", "https://api.nuget.org/v3/index.json" }
            });

        // A function for quickly retrieving the latest versions of our packages across various
        // nuget sources. Good for determining next versions.
        [FunctionName("GetLatestNugetVersions")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            IList<NugetSourceResult> results = new List<NugetSourceResult>();
            IList<Task<NugetSourceResult>> tasks = new List<Task<NugetSourceResult>>();

            bool preRelease = false;
            if (bool.TryParse(req.Query["preRelease"], out bool preReleaseQuery))
            {
                preRelease = preReleaseQuery;
            }

            foreach (var source in nugetSources)
            {
                tasks.Add(GetResult(source.Key, source.Value, preRelease));
            }

            await Task.WhenAll(tasks);

            foreach (var resultTask in tasks)
            {
                results.Add(await resultTask);
            }

            return new JsonResult(results);
        }

        private static async Task<NugetSourceResult> GetResult(string sourceName, string sourceUri, bool preRelease)
        {
            var result = new NugetSourceResult(sourceName, sourceUri);
            string searchUri = await GetSearchUri(sourceUri);

            IList<Task<NugetPackage>> tasks = new List<Task<NugetPackage>>();

            foreach (var package in packageNames)
            {
                tasks.Add(GetLatestVersion(searchUri, package, preRelease));
            }

            await Task.WhenAll(tasks);

            foreach (var packageTask in tasks)
            {
                result.AddPackage(await packageTask);
            }

            return result;
        }

        private static async Task<NugetPackage> GetLatestVersion(string searchUri, string packageName, bool preRelease)
        {
            string latestVersion = null;
            HttpResponseMessage response = await _client.GetAsync($"{searchUri}?q=PackageId:{packageName}&prerelease={preRelease}");
            var responseData = await response.Content.ReadAsAsync<JObject>();
            JArray data = responseData["data"] as JArray;

            // Some of our feeds may not have the package currently
            if (data.Any())
            {
                JArray versions = data.Single()["versions"] as JArray;
                var last = versions.OrderBy(p => p["version"]).Last()["version"];
                latestVersion = last.ToString();
            }

            return new NugetPackage(packageName, latestVersion);
        }

        private static async Task<string> GetSearchUri(string indexUri)
        {
            HttpResponseMessage response = await _client.GetAsync(indexUri);
            JObject responseData = await response.Content.ReadAsAsync<JObject>();
            JArray apis = responseData["resources"] as JArray;
            JToken searchApi = apis.First(p => p["@type"].ToString() == "SearchQueryService");
            return searchApi["@id"].ToString();
        }

        private class NugetSourceResult
        {
            private IList<NugetPackage> _packages = new List<NugetPackage>();

            public NugetSourceResult(string name, string url)
            {
                SourceName = name;
                SourceUrl = url;
            }

            public string SourceName { get; set; }

            public string SourceUrl { get; set; }

            public NugetPackage[] Packages => _packages.ToArray();

            public void AddPackage(NugetPackage package)
            {
                _packages.Add(package);
            }
        }

        private class NugetPackage
        {
            public NugetPackage(string name, string newestVersion)
            {
                Name = name;
                NewestVersion = newestVersion;
            }

            public string Name { get; set; }

            public string NewestVersion { get; set; }
        }
    }
}
