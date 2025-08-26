using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Spectre.Console;
using System.Threading.RateLimiting;
using Polly;

namespace darkorbit_resource_downloader
{
	public class File
	{
        [XmlAttribute("debugView")]
		public bool DebugView { get; set; }

		[XmlAttribute("hash")]
		public string Hash { get; set; }

		[XmlAttribute("id")]
		public string Id { get; set; }

		[XmlAttribute("location")]
		public string Location { get; set; }

		[XmlAttribute("name")]
		public string Name { get; set; }

		[XmlAttribute("type")]
		public string Type { get; set; }

		[XmlAttribute("version")]
		public int Version { get; set; }

		public static bool operator ==(File lhs, File rhs)
		{
			return lhs?.Hash == rhs?.Hash && lhs?.Id == rhs?.Id && lhs?.Location == rhs?.Location && lhs?.Type == rhs?.Type &&
			       lhs?.Version == rhs?.Version;
		}

		public static bool operator !=(File lhs, File rhs)
		{
			return !(lhs == rhs);
		}
	}

	public class Location
	{
		[XmlAttribute("id")]
		public string Id { get; set; }
		[XmlAttribute("path")]
		public string Path { get; set; }
	}

	[XmlRoot("filecollection")]
	public class FileCollection
	{
		[XmlElement("location")]
		public List<Location> Locations { get; set; }

		[XmlElement("file")]
		public List<File> Files { get; set; }
	}

    internal static class Program
    {
        private static FileCollection RemoteCollection { get; set; }
        private static FileCollection LocalCollection { get; set; }
        private static int _skippedFiles;
        private static int _totalFiles;
        private static int _maxParallel = 10;
        private static double _rps = 6; // requests per second budget
        private static int _burst = 6;   // burst tokens
        private static int _retries = 3; // retry attempts
        private static HttpClient Http = default!;
        private static RateLimiter _rateLimiter = default!;

        private static HttpClient CreateHttpClient(int maxConnections)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                MaxConnectionsPerServer = Math.Max(1, maxConnections),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("darkorbit-resource-downloader/1.0");
            return client;
        }

        private static RateLimiter CreateRateLimiter(double rps, int burst)
        {
            var tokensPerPeriod = Math.Max(1, (int)Math.Ceiling(rps));
            return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, burst),
                QueueLimit = 0,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = tokensPerPeriod,
                AutoReplenishment = true
            });
        }

        private static async Task Main(string[] args)
        {
            ParseArgs(args);
            Http = CreateHttpClient(_maxParallel);
            _rateLimiter = CreateRateLimiter(_rps, _burst);
            // Download additional standalone SWFs first
            var xmlFiles = new List<string>
            {
                "https://darkorbit-22.bpsecure.com/spacemap/xml/resources.xml",
                "https://darkorbit-22.bpsecure.com/spacemap/xml/resources_3d.xml",
                "https://darkorbit-22.bpsecure.com/do_img/global/xml/resource_items.xml",
                "https://darkorbit-22.bpsecure.com/swf_global/inventory/xml/assets.xml",
                "https://darkorbit-22.bpsecure.com/swf_global/xml/assets.xml",
            };

            foreach (var xmlFileUrl in xmlFiles)
            {
                await ParseAndDownloadXmlFile(xmlFileUrl);
            }
            await DownloadAdditionalFilessAsync();
            Console.ReadLine();
        }

        private static async Task DownloadAdditionalFilessAsync()
        {
            var urls = new List<string>
            {
                "https://darkorbit-22.bpsecure.com/spacemap/main.swf",
                "https://darkorbit-22.bpsecure.com/swf_global/inventory/inventory.swf",
                "https://darkorbit-22.bpsecure.com/spacemap/graphics/maps-config.xml",
                "https://darkorbit-22.bpsecure.com/spacemap/graphics/spacemap-config.xml",
                "https://darkorbit-22.bpsecure.com/spacemap/templates/en/flashres.xml",
            };

            AnsiConsole.Write(new Rule("[green]Downloading additional Files[/]"));

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var throttler = new SemaphoreSlim(_maxParallel);
                    var tasks = new List<Task>();

                    foreach (var url in urls)
                    {
                        await throttler.WaitAsync();
                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                await DownloadDirectUrlWithProgress(ctx, url);
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        });
                        tasks.Add(t);
                    }

                    await Task.WhenAll(tasks);
                });
        }

        private static async Task DownloadDirectUrlWithProgress(ProgressContext ctx, string url)
        {
            var uri = new Uri(url);
            var relativePath = uri.AbsolutePath.TrimStart('/');
            var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory($"do_resources/{directory}");
            }

            var fileName = Path.GetFileName(relativePath);
            var filePath = string.IsNullOrEmpty(directory)
                ? $"do_resources/{fileName}"
                : $"do_resources/{directory}/{fileName}";

            if (System.IO.File.Exists(filePath))
                return;

            var task = ctx.AddTask(relativePath);

            using var response = await SendWithRetryAsync(url);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            if (contentLength.HasValue && contentLength.Value > 0)
            {
                task.MaxValue = contentLength.Value;
            }
            else
            {
                task.MaxValue = 100; // fallback when size unknown
            }

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                if (contentLength is > 0)
                {
                    task.Value = Math.Min(totalRead, contentLength.Value);
                }
                else
                {
                    task.Increment(1);
                    if (task.Value >= task.MaxValue)
                        task.Value = 0;
                }
            }

            task.StopTask();
        }

        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--max-parallel", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var mp) && mp > 0)
                    {
                        _maxParallel = mp;
                        i++;
                    }
                }
                else if (string.Equals(a, "--rps", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out var rps) && rps > 0)
                    {
                        _rps = rps;
                        i++;
                    }
                }
                else if (string.Equals(a, "--burst", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var burst) && burst > 0)
                    {
                        _burst = burst;
                        i++;
                    }
                }
                else if (string.Equals(a, "--retries", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var retries) && retries >= 0)
                    {
                        _retries = retries;
                        i++;
                    }
                }
            }
        }

        private static readonly Regex XmlPattern = new Regex(@"(.+)xml\/(.+\.xml)", RegexOptions.Compiled);
        private static async Task ParseAndDownloadXmlFile(string url)
        {
            var match = XmlPattern.Match(url);

            var baseUrl = match.Groups[1].Value;
			var xmlFile = match.Groups[2].Value;
            if (System.IO.File.Exists(xmlFile))
            {
                LocalCollection = XmlToT<FileCollection>(await System.IO.File.ReadAllTextAsync(xmlFile));
            }
            else
            {
                LocalCollection = new FileCollection
                {
                    Files = [],
                    Locations = []
                };
            }

            var resourceXml = await GetStringWithRetryAsync(url);
            RemoteCollection = XmlToT<FileCollection>(resourceXml);
            _totalFiles = RemoteCollection.Files.Count;
            await System.IO.File.WriteAllTextAsync(xmlFile, resourceXml);

			AnsiConsole.MarkupLine($"Found {RemoteCollection.Files.Count} files for [yellow]{xmlFile}[/].");

            var sw = new Stopwatch();
            sw.Start();

            AnsiConsole.Write(new Rule($"[green]Downloading {xmlFile}[/]"));

            // Process every file in the XML (no filtering).
            var filesToProcess = RemoteCollection.Files.AsEnumerable();

            int skipped = 0;
            int missing = 0;
            int failed = 0;
            int total = filesToProcess.Count();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var xmlTask = ctx.AddTask($"[green]{xmlFile}[/]");
                    xmlTask.MaxValue = total;

                    var throttler = new SemaphoreSlim(_maxParallel);
                    var tasks = new List<Task>();

                    foreach (var f in filesToProcess)
                    {
                        await throttler.WaitAsync();
                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                var outcome = await DownloadFileAsync(baseUrl, f);
                                switch (outcome)
                                {
                                    case DownloadOutcome.Skipped:
                                        Interlocked.Increment(ref skipped);
                                        break;
                                    case DownloadOutcome.Missing:
                                        Interlocked.Increment(ref missing);
                                        break;
                                    case DownloadOutcome.Failed:
                                        Interlocked.Increment(ref failed);
                                        break;
                                }
                            }
                            finally
                            {
                                xmlTask.Increment(1);
                                throttler.Release();
                            }
                        });
                        tasks.Add(t);
                    }

                    await Task.WhenAll(tasks);
                });

            sw.Stop();
            AnsiConsole.MarkupLine($"[bold green]Done![/] Elapsed: [yellow]{sw.Elapsed}[/]. Total [yellow]{total}[/], skipped [yellow]{skipped}[/], missing (404) [yellow]{missing}[/], failed [yellow]{failed}[/].");
		}

        private enum DownloadOutcome { Downloaded, Skipped, Missing, Failed }

        private static async Task<DownloadOutcome> DownloadFileAsync(string baseUrl, File file)
        {
            var loc = RemoteCollection.Locations.FirstOrDefault(loc => loc.Id == file.Location);
            if (loc == null)
            {
                AnsiConsole.MarkupLine($"[red]Unable to find location for[/] [yellow]{file.Location}[/]");
                return DownloadOutcome.Failed;
            }
            var location = loc.Path;
            Directory.CreateDirectory($"do_resources/{location}");

            var filePath = $"do_resources/{location}{file.Name}.{file.Type}";

            var localFile = LocalCollection.Files.Find(f => f == file);
            if (System.IO.File.Exists(filePath) && localFile?.Hash == file.Hash)
            {
                return DownloadOutcome.Skipped;
            }
            else
            {
                // Try a few URL variants to match how the client/CDN might expose files
                var candidates = new List<string>
                {
                    BuildFileUrl(baseUrl, file, lowercaseType: false, includeHash: true),
                    BuildFileUrl(baseUrl, file, lowercaseType: true, includeHash: true),
                    BuildFileUrl(baseUrl, file, lowercaseType: false, includeHash: false),
                    BuildFileUrl(baseUrl, file, lowercaseType: true, includeHash: false)
                };

                foreach (var url in candidates.Distinct())
                {
                    using var response = await SendWithRetryAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                            continue; // try next variant
                        return DownloadOutcome.Failed;
                    }

                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                    }
                    return DownloadOutcome.Downloaded;
                }

                return DownloadOutcome.Missing;
            }
        }

        private static string BuildFileUrl(string baseUrl, File file, bool lowercaseType, bool includeHash)
        {
            var typePart = lowercaseType ? file.Type?.ToLowerInvariant() : file.Type;
            var url = $"{baseUrl}{RemoteCollection.Locations.First(l => l.Id == file.Location).Path}{file.Name}.{typePart}";
            if (includeHash && !string.IsNullOrWhiteSpace(file.Hash))
                url += (url.Contains('?') ? "&" : "?") + "__cv=" + file.Hash;
            return url;
        }

        private static async Task<HttpResponseMessage> SendWithRetryAsync(string url)
        {
            // Simple async retry with jitter and respect Retry-After when present
            for (int attempt = 1; attempt <= Math.Max(1, _retries + 1); attempt++)
            {
                using var lease = await _rateLimiter.AcquireAsync(1);
                var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode < 400)
                    return response;

                // 429 or 5xx: decide to retry
                if (attempt > _retries || (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500))
                {
                    return response; // caller will EnsureSuccessStatusCode or handle
                }

                TimeSpan delay = ComputeDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay);
            }

            // Should not reach here
            return await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }

        private static async Task<string> GetStringWithRetryAsync(string url)
        {
            using var resp = await SendWithRetryAsync(url);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync();
        }

        private static TimeSpan ComputeDelay(HttpResponseMessage response, int attempt)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (response.Headers.RetryAfter != null)
                {
                    if (response.Headers.RetryAfter.Delta.HasValue)
                        return response.Headers.RetryAfter.Delta.Value + Jitter(250);
                    if (response.Headers.RetryAfter.Date.HasValue)
                    {
                        var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                        if (delta > TimeSpan.Zero)
                            return delta + Jitter(250);
                    }
                }
                return TimeSpan.FromSeconds(1) + Jitter(250);
            }
            // Exponential backoff with jitter
            double baseMs = 500 * Math.Pow(2, attempt - 1);
            baseMs = Math.Min(baseMs, 8000);
            return TimeSpan.FromMilliseconds(baseMs) + Jitter(250);
        }

        private static TimeSpan Jitter(int maxMs)
        {
            var rand = System.Security.Cryptography.RandomNumberGenerator.Create();
            var bytes = new byte[4];
            rand.GetBytes(bytes);
            int v = BitConverter.ToInt32(bytes, 0) & int.MaxValue;
            return TimeSpan.FromMilliseconds(v % Math.Max(1, maxMs));
        }

		private static T XmlToT<T>(string xml)
		{
			var serializer = new XmlSerializer(typeof(T));
            using TextReader reader = new StringReader(xml);
            return (T)serializer.Deserialize(reader);
        }
	}
}
