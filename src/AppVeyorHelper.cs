using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FunctionsBuildHelper
{
    public class AppVeyorClient
    {
        // Cache some values in-memory so we don't need to make the calls every time.
        private static ConcurrentDictionary<string, Task<Project>> _projectMap = new ConcurrentDictionary<string, Task<Project>>();
        private static ConcurrentDictionary<string, Task<Job[]>> _buildJobMap = new ConcurrentDictionary<string, Task<Job[]>>();
        private static ConcurrentDictionary<string, Task<Artifact[]>> _artifactMap = new ConcurrentDictionary<string, Task<Artifact[]>>();

        private string _apiKey = Environment.GetEnvironmentVariable("AppVeyorToken");

        private HttpClient _httpClient = Helper.HttpClient;

        public string Endpoint { get; set; } = "https://ci.appveyor.com/api";

        private AppVeyorClient()
        {
        }

        private static Lazy<AppVeyorClient> _lazyClient = new Lazy<AppVeyorClient>(() => new AppVeyorClient());

        public static AppVeyorClient Instance => _lazyClient.Value;

        public Task<Project> GetProjectByNameAsync(string name)
        {
            return _projectMap.GetOrAdd(name, async n =>
            {
                var projects = await GetProjectsAsync();
                return projects.Where(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            });
        }

        public async Task<Project[]> GetProjectsAsync()
        {
            var url = "/projects";
            var list = await SendAsync<Project[]>(HttpMethod.Get, url);
            return list;
        }

        internal async Task<T> SendAsync<T>(HttpMethod method, string url)
        {
            var fullUrl = this.Endpoint + "/" + url;
            HttpRequestMessage request = new HttpRequestMessage(method, fullUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Boo: " + url);
            }

            string json = await response.Content.ReadAsStringAsync();
            T result = JsonConvert.DeserializeObject<T>(json);
            return result;
        }

        public async Task<Stream> GetArtifactStreamAsync(string jobId, string artifactPath)
        {
            return await _httpClient.GetStreamAsync($"{Endpoint}/buildjobs/{jobId}/artifacts/{artifactPath}");
        }

        public async Task<HistoryResponse> GetBuildHistoryAsync(Project project, string branch, int? startBuildId = null)
        {
            // GET /api/projects/appsvc/azure-webjobs-sdk-script-y8o14/history?recordsNumber=10&startbuildId=9817916
            var url = $"/projects/{project.accountName}/{project.slug}/history?recordsNumber=50&branch={branch}";
            if (startBuildId != null)
            {
                url += "&startbuildId=" + startBuildId;
            }

            var list = await SendAsync<HistoryResponse>(HttpMethod.Get, url);
            return list;
        }

        public Task<Job[]> GetJobInfoAsync(Project project, string buildVersion)
        {
            // GET /api/projects/appsvc/azure-webjobs-sdk-script-y8o14/build/1.0.11033-sshumfpu
            var url = $"/projects/{project.accountName}/{project.slug}/build/{buildVersion}";

            return _buildJobMap.GetOrAdd(url, async u =>
            {
                var list = await SendAsync<BuildDetailsResponse>(HttpMethod.Get, u);
                return list.build.jobs;
            });
        }

        public async Task<JobTestResults> GetTestResultsAsync(Job job)
        {
            //  GET https://ci.appveyor.com/api/buildjobs/a6od8bwe9rg0oy0i/tests HTTP/1.1
            var url = $"/buildjobs/{job.jobId}/tests";

            var list = await SendAsync<JobTestResults>(HttpMethod.Get, url);
            return list;
        }

        public async Task<Artifact[]> GetArtifactsAsync(Project project, string buildVersion)
        {
            var jobs = await GetJobInfoAsync(project, buildVersion);
            var jobId = jobs.Single().jobId;

            return await GetArtifactsAsync(jobId);
        }

        public Task<Artifact[]> GetArtifactsAsync(string jobId)
        {
            var url = $"/buildjobs/{jobId}/artifacts";

            return _artifactMap.GetOrAdd(url, async u =>
            {
                return await SendAsync<Artifact[]>(HttpMethod.Get, u);
            });
        }
    }

    public class HistoryResponse
    {
        public Project project { get; set; }
        public Build[] builds { get; set; }
    }

    public class BuildDetailsResponse
    {
        public Project project { get; set; }
        public Build build { get; set; }
    }

    public class Build
    {
        public string authorName { get; set; }
        public string branch { get; set; }
        public int buildId { get; set; }
        public int buildNumber { get; set; }
        public BuildStatus status { get; set; } // "failed"
        public string version { get; set; } // "1.0.11033-sshumfpu"
        public string pullRequestId { get; set; }
        public DateTimeOffset finished { get; set; }
        public Job[] jobs { get; set; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum Status
    {
        passed,
        failed,
        running,
        skipped
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum BuildStatus
    {
        success,
        failed,
        queued,
        running,
        cancelled,
    }

    public class Job
    {
        public int failedTestsCount { get; set; }
        public string jobId { get; set; }
        public string status { get; set; } // Failed
        public int testsCount { get; set; }
    }

    public class JobTestResults
    {
        public int failed { get; set; }
        public int passed { get; set; }
        public int total { get; set; }

        public Entry[] list { get; set; }

        public class Entry
        {
            public string fileName { get; set; }
            public string name { get; set; }
            public Status outcome { get; set; } // "passed"
            public int duration { get; set; }
            public DateTimeOffset created { get; set; }
        }

        // These are added by us to help with logging
        public string BuildNumber { get; set; }
    }



    public class Project
    {
        public string accountId { get; set; }
        public string accountName { get; set; }

        public string name { get; set; }


        public string projectId { get; set; }
        public string slug { get; set; } // "azure-webjobs-sdk-script-y8o14"

        public string repositoryName { get; set; } // "Azure/azure-webjobs-sdk-script"      
        public string repositoryBranch { get; set; } // "dev"      


        public override string ToString()
        {
            return this.name;
        }
    }

    public class Artifact
    {
        public string fileName { get; set; }
    }
}