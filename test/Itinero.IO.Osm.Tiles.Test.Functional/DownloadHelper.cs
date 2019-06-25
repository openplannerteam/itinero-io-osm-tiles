using System;
using System.IO;
using System.Net.Http;
using System.Web;
using Itinero.Logging;

namespace Itinero.IO.Osm.Tiles.Test.Functional
{
    internal static class DownloadHelper
    {
        /// <summary>
        /// Gets a stream for the content at the given url.
        /// </summary>
        /// <param name="url">The url.</param>
        /// <returns>An open stream for the content at the given url.</returns>
        public static Stream Download(string url)
        {
            var fileName = HttpUtility.UrlEncode(url);
            fileName = Path.Combine(".", "cache", fileName);

            if (File.Exists(fileName))
            {
                return File.OpenRead(fileName);
            }
                
            try
            {
                var client = new HttpClient();
                var response = client.GetAsync(url);
                using (var stream = response.GetAwaiter().GetResult().Content.ReadAsStreamAsync().GetAwaiter()
                    .GetResult()) 
                using (var fileStream = File.Open(fileName, FileMode.Create))
                {
                    stream.CopyTo(fileStream);    
                }
            }
            catch (Exception ex)
            {
                Itinero.Logging.Logger.Log(nameof(DownloadHelper), TraceEventType.Warning, 
                    $"Failed to download from {url}: {ex}.");
                return null;
            }
            
            return File.OpenRead(fileName);
        }
    }
}