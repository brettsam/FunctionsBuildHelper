using System;
using System.Collections.Generic;
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
            "Microsoft.Azure.WebJobs.Logging.ApplicationInsights",
            "Microsoft.NET.Sdk.Functions",
            "Microsoft.Azure.Functions.Extensions",
            "Microsoft.Azure.WebJobs.Script.ExtensionsMetadataGenerator"
        };

        private static readonly NugetRegistration[] nugetRegistrations = new[]
        {
            new NugetRegistration("App Service Nightly", "https://www.myget.org/F/azure-appservice/api/v3/index.json", "https://www.myget.org/feed/azure-appservice/package/nuget/{id}/{version}"),
            new NugetRegistration("App Service Staging", "https://www.myget.org/F/azure-appservice-staging/api/v3/index.json", "https://www.myget.org/feed/azure-appservice-staging/package/nuget/{id}/{version}" ),
            new NugetRegistration("Nuget.org", "https://api.nuget.org/v3/index.json" )
        };

        // A function for quickly retrieving the latest versions of our packages across various
        // nuget sources. Good for determining next versions.
        [FunctionName("GetLatestNugetVersions")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            IList<NugetSourceResult> results = new List<NugetSourceResult>();
            IList<Task<NugetSourceResult>> tasks = new List<Task<NugetSourceResult>>();

            foreach (var source in nugetRegistrations)
            {
                tasks.Add(GetResult(source));
            }

            await Task.WhenAll(tasks);

            foreach (var resultTask in tasks)
            {
                results.Add(await resultTask);
            }

            return new JsonResult(results);
        }

        private static async Task<NugetSourceResult> GetResult(NugetRegistration registration)
        {
            NugetSourceResult result = await GetNugetSource(registration);

            IList<Task<NugetPackage>> tasks = new List<Task<NugetPackage>>();

            foreach (var package in packageNames)
            {
                tasks.Add(GetLatestVersion(result.SearchUrl, package, result.PackageDetailsUrlTemplate));
            }

            await Task.WhenAll(tasks);

            foreach (var packageTask in tasks)
            {
                result.AddPackage(await packageTask);
            }

            return result;
        }

        private static async Task<NugetPackage> GetLatestVersion(string searchUri, string packageName, string packageDetailsUriTemplate)
        {
            // call it twice; once for prerelease, once not
            async Task<string> GetLatestVersion(bool preRelease)
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

                return latestVersion;
            }

            string version = await GetLatestVersion(false);
            string preReleaseVersion = await GetLatestVersion(true);

            // construct uri to the main page of this package (no version)
            string packageDetailsUri = packageDetailsUriTemplate.Replace("{id}", packageName).Replace("{version}", string.Empty).TrimEnd('/');

            return new NugetPackage(packageName, version, preReleaseVersion, packageDetailsUri);
        }

        private static async Task<NugetSourceResult> GetNugetSource(NugetRegistration registration)
        {
            HttpResponseMessage response = await _client.GetAsync(registration.ServiceUri);
            JObject responseData = await response.Content.ReadAsAsync<JObject>();
            JArray apis = responseData["resources"] as JArray;
            JToken searchApi = apis.First(p => p["@type"].ToString() == "SearchQueryService");
            JToken packageDetailsUriTemplateJson = apis.FirstOrDefault(p => p["@type"].ToString().StartsWith("PackageDetailsUriTemplate"));
            string packageDetailsUriTemplate = packageDetailsUriTemplateJson == null ? registration.FallbackPackageDetailsUriTemplate : packageDetailsUriTemplateJson["@id"].ToString();

            return new NugetSourceResult(registration.FriendlyName, registration.ServiceUri, searchApi["@id"].ToString(), packageDetailsUriTemplate);
        }

        private class NugetSourceResult
        {
            private IList<NugetPackage> _packages = new List<NugetPackage>();

            public NugetSourceResult(string name, string url, string searchUrl, string packageDetailsUrlTemplate)
            {
                SourceName = name;
                SourceUrl = url;
                SearchUrl = searchUrl;
                PackageDetailsUrlTemplate = packageDetailsUrlTemplate;
            }

            public string SourceName { get; private set; }

            public string SourceUrl { get; private set; }

            public string SearchUrl { get; private set; }

            public string PackageDetailsUrlTemplate { get; private set; }

            public NugetPackage[] Packages => _packages.ToArray();

            public void AddPackage(NugetPackage package)
            {
                _packages.Add(package);
            }
        }

        private class NugetPackage
        {
            public NugetPackage(string name, string newestVersion, string newestPreReleaseVersion, string packageUri)
            {
                Name = name;
                NewestVersion = newestVersion;
                NewestPreReleaseVersion = newestPreReleaseVersion;
                PackageUri = new Uri(packageUri);
            }

            public string Name { get; private set; }

            public string NewestVersion { get; private set; }

            public string NewestPreReleaseVersion { get; private set; }

            public Uri PackageUri { get; private set; }
        }

        private class NugetRegistration
        {
            public NugetRegistration(string friendlyName, string serviceUri, string fallbackPackageDetailsUriTemplate = null)
            {
                FriendlyName = friendlyName;
                ServiceUri = serviceUri;
                FallbackPackageDetailsUriTemplate = fallbackPackageDetailsUriTemplate;
            }

            public string FriendlyName { get; private set; }


            public string ServiceUri { get; private set; }

            /// <summary>
            /// Used to generate the browseable Uri link to the package. MyGet doesn't contain this in its
            /// service feed, but Nuget.org does. Needs to be in the format "https://site.org/{id}/{version}"
            /// </summary>
            public string FallbackPackageDetailsUriTemplate { get; private set; }
        }
    }
}
