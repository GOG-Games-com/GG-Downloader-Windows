using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace gg_downloader.Services
{
    internal class HttpRequest
    {

        static readonly HttpClient client = new HttpClient();

        public static async Task< HttpResponseMessage> Get(Uri uri)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

            HttpResponseMessage response = await client.GetAsync(uri);
            return response;
        }

        public static async Task<HttpResponseMessage> Get(Uri uri, string username, string password)
        {

            byte[] byteArray = Encoding.ASCII.GetBytes($"{username}:{password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GOG-Downloader-Win/1.0");

            HttpResponseMessage response = await client.GetAsync(uri);
            return response;
        }

    }
}
