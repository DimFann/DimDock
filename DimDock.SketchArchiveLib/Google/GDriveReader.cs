using System;
using System.Net;

using Newtonsoft.Json;
using RestSharp;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace SketchArchiveLib.Google
{
    public class GDriveReader
    {
        public string LastError;

        private readonly RestClient _restClient;
        private readonly string _apiKey;
        private readonly string _apiUrlGetFiles;

        public GDriveReader(string apiKey, string apiUrlGetFiles)
        {
            _restClient = new RestClient();
            apiKey = apiKey.Trim();
            if (string.IsNullOrEmpty(apiKey))
                throw new Exception("Bad API key");

            _apiKey = apiKey;
            _apiUrlGetFiles = apiUrlGetFiles;
        }

        public async Task<string> GetFolderDescription(string folderID, string resourceKey)
        {
            RestRequest request = new(_apiUrlGetFiles + $"/{folderID}", Method.Get);

            if (!string.IsNullOrWhiteSpace(resourceKey))
                request.AddHeader("X-Goog-Drive-Resource-Keys", $"{folderID}/{resourceKey}");
            request.AddParameter("fields", "description");
            request.AddParameter("key", _apiKey);
            
            RestResponse response = await _restClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                LastError = "";
                JsonObject jo = JsonObject.Parse(response.Content).AsObject();
                return jo["description"]?.ToString();
            }

#if DEV
            else
                throw new Exception("You probably need to add the server IP to the API credentials page.");
#else
            LastError = (int)response.StatusCode + $" {response.ErrorMessage}";
            return null;
#endif
        }

        public async Task<GDriveFiles> GetFolderContentsAsync(string folderID, string resourceKey)
        {
            RestRequest request = new (_apiUrlGetFiles, Method.Get);

            if (!string.IsNullOrWhiteSpace(resourceKey))
                request.AddHeader("X-Goog-Drive-Resource-Keys", $"{folderID}/{resourceKey}");

            request.AddParameter("fields", "files(id,name,mimeType,parents,resourceKey,shortcutDetails,modifiedTime)");
            request.AddParameter("key", _apiKey);
            request.AddParameter("q", $"'{folderID}' in parents");
            request.AddParameter("pageSize", "1000");

            RestResponse response = await _restClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                LastError = "";
                var gdf = JsonConvert.DeserializeObject<GDriveFiles>(response.Content);

                try
                {
                    string description = await GetFolderDescription(folderID,resourceKey);
                    if(!string.IsNullOrWhiteSpace(description))
                        gdf.Description = description;
                }
                catch(Exception e)
                {
                    // TODO: Log this or something.
                }

                return gdf;
            }

#if DEV
            else
                throw new Exception("You probably need to add the server IP to the API credentials page.");
#else
            LastError = (int)response.StatusCode + $" {response.ErrorMessage}";
            return null;
#endif
        }
    }
}