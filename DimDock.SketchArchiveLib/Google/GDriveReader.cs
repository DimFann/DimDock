using System;
using System.Net;

using Newtonsoft.Json;
using RestSharp;
using System.Threading.Tasks;

namespace SketchArchiveLib.Google
{
    public class GDriveReader
    {
        public string LastError;

        private readonly RestClient _restClient;
        private readonly string _apiKey;
        private readonly string _apiUrlGetFile;
        private readonly string _apiUrlGetFiles;

        public GDriveReader(string apiKey, string apiUrlGetFile, string apiUrlGetFiles)
        {
            _restClient = new RestClient();
            apiKey = apiKey.Trim();
            if (string.IsNullOrEmpty(apiKey))
                throw new Exception("Bad API key");

            _apiKey = apiKey;
            _apiUrlGetFile = apiUrlGetFile;
            _apiUrlGetFiles = apiUrlGetFiles;
        }

        public async Task<GDriveItem> GetDriveItemAsync(string id, string resourceKey)
        {
            RestRequest request = new RestRequest(_apiUrlGetFile, Method.Get);

            if (!string.IsNullOrWhiteSpace(resourceKey))
                request.AddHeader("X-Goog-Drive-Resource-Keys", $"{id}/{resourceKey}");

            request.AddParameter("key", _apiKey);
            request.AddParameter("fields", "id,name,mimeType,parents,description,resourceKey,shortcutDetails");
            request.AddParameter("fileId", id);

            RestResponse response = await _restClient.ExecuteAsync(request);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                LastError = "";
                return JsonConvert.DeserializeObject<GDriveItem>(response.Content);
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
            RestRequest request = new RestRequest(_apiUrlGetFiles, Method.Get);

            if (!string.IsNullOrWhiteSpace(resourceKey))
                request.AddHeader("X-Goog-Drive-Resource-Keys", $"{folderID}/{resourceKey}");

            request.AddParameter("fields", "files(id,name,mimeType,parents,description,resourceKey,shortcutDetails)");
            request.AddParameter("key", _apiKey);
            request.AddParameter("q", $"'{folderID}' in parents");
            request.AddParameter("pageSize", "1000");

            RestResponse response = await _restClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                LastError = "";
                var gdf = JsonConvert.DeserializeObject<GDriveFiles>(response.Content);
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