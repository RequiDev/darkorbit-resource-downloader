using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

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

	internal class Program
	{
		private static FileCollection RemoteCollection { get; set; }
		private static FileCollection LocalCollection { get; set; }
		private static int _skippedFiles;
		private static int _totalFiles;

		private static void Main(string[] args)
        {
            var xmlFiles = new List<string>()
            {
                "https://darkorbit-22.bpsecure.com/spacemap/xml/resources.xml",
                "https://darkorbit-22.bpsecure.com/spacemap/xml/resources_3d.xml",
                "https://darkorbit-22.bpsecure.com/do_img/global/xml/resource_items.xml"
            };

			Console.WriteLine($"You can choose from these xml files to parse and download:");
            for (var i = 0; i < xmlFiles.Count; i++)
            {
                const string pattern = @"(.+)xml\/(.+\.xml)";
                var match = Regex.Match(xmlFiles[i], pattern);
                var file = match.Groups[2].Value;

				Console.WriteLine($"{i} - {file}");
            }

			Console.Write("Type in your desired index to download the file. Anything else to download all of them: ");

            var input = Console.ReadLine();
            if (!int.TryParse(input, out var index) || index >= xmlFiles.Count)
            {
                foreach (var xmlFile in xmlFiles)
                {
					ParseAndDownloadXmlFile(xmlFile);
                }
            }
            else
            {
                ParseAndDownloadXmlFile(xmlFiles[index]);
			}
		}

        private static void ParseAndDownloadXmlFile(string url)
        {
            const string pattern = @"(.+)xml\/(.+\.xml)";
            var match = Regex.Match(url, pattern);

            var baseUrl = match.Groups[1].Value;
			var xmlFile = match.Groups[2].Value;
            if (System.IO.File.Exists(xmlFile))
            {
                LocalCollection = XmlToT<FileCollection>(System.IO.File.ReadAllText(xmlFile));
            }
            else
            {
                LocalCollection = new FileCollection
                {
                    Files = new List<File>(),
                    Locations = new List<Location>()
                };
            }

            var resourceXml = new WebClient().DownloadString(url);
            RemoteCollection = XmlToT<FileCollection>(resourceXml);
            _totalFiles = RemoteCollection.Files.Count;
            System.IO.File.WriteAllText(xmlFile, resourceXml);

            Console.Write($"Found {RemoteCollection.Files.Count} files to download for {xmlFile}. Do you want to continue? (y/n): ");
            var answer = Console.ReadKey();
            if (answer.Key != ConsoleKey.Y)
                return;

            var sw = new Stopwatch();
            Console.WriteLine("\nDownloading files now...");
            sw.Start();
            Parallel.ForEach(RemoteCollection.Files, new ParallelOptions
            {
                MaxDegreeOfParallelism = 10
            }, f =>
            {
                DownloadFile(baseUrl, f);
            });
            sw.Stop();
            Console.WriteLine($"Done! Downloaded in {sw.Elapsed}. Skipped {_skippedFiles}/{_totalFiles} Files.");
            Console.ReadLine();
		}

		private static void DownloadFile(string baseUrl, File file)
		{
			var location = RemoteCollection.Locations.Find(loc => loc.Id == file.Location).Path;
			Directory.CreateDirectory($"do_resources/{location}");

			var filePath = $"do_resources/{location}{file.Name}.{file.Type}";

			var localFile = LocalCollection.Files.Find(f => f == file);
			if (System.IO.File.Exists(filePath) && localFile?.Hash == file.Hash)
			{
				Console.WriteLine($"Skipped {file.Name}.{file.Type}");
				Interlocked.Increment(ref _skippedFiles);
			}
			else
			{
				var url = $"{baseUrl}{location}{file.Name}.{file.Type}";

                using var wc = new WebClient();
                wc.DownloadFile(new Uri(url), filePath);

				Console.WriteLine($"Downloaded {file.Name}.{file.Type}");
			}
		}

		private static T XmlToT<T>(string xml)
		{
			var serializer = new XmlSerializer(typeof(T));
            using TextReader reader = new StringReader(xml);
            return (T)serializer.Deserialize(reader);
        }
	}
}
