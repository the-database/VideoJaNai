using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;


namespace AnimeJaNaiConverterGui.Services
{
    public class Downloader
    {
        public delegate void ProgressChanged(double percentage);

        public static async Task DownloadFileAsync(string url, string destinationFilePath, ProgressChanged progressChanged, int maxRetries = 10)
        {
            long totalBytes = 0;
            long totalRead = 0;
            int retryCount = 0;
            bool downloadComplete = false;

            while (!downloadComplete && retryCount < maxRetries)
            {
                try
                {
                    using HttpClient client = new();
                    using HttpRequestMessage request = new(HttpMethod.Get, url)
                    {
                        Version = HttpVersion.Version30
                    };

                    if (totalRead > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(totalRead, null); // resume
                    }

                    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using Stream contentStream = await response.Content.ReadAsStreamAsync(),
                                 fileStream = new FileStream(destinationFilePath, FileMode.Append, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    int read;

                    while ((read = await contentStream.ReadAsync(buffer)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        if (totalBytes != -1)
                        {
                            double percentage = Math.Round((double)totalRead / totalBytes * 100, 0);
                            progressChanged?.Invoke(percentage);
                        }
                    }

                    downloadComplete = true;
                }
                catch (HttpRequestException e)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        throw; // Re-throw if max retries are reached
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * retryCount));
                }
                catch (TaskCanceledException e)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        throw new Exception("The download was canceled or timed out.", e);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2 * retryCount));
                }
            }
        }
    }
}
