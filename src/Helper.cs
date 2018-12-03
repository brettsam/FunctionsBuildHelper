using System;
using System.Net.Http;

namespace FunctionsBuildHelper
{
    public static class Helper
    {
        private static Lazy<HttpClient> _lazyClient = new Lazy<HttpClient>(() =>
        {
            return new HttpClient(new HttpClientHandler
            {
                MaxConnectionsPerServer = 50
            });
        });

        public static HttpClient HttpClient => _lazyClient.Value;
    }
}
