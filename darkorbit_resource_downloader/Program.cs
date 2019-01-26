using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
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
		public float Version { get; set; }
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

	class Program
	{
		public static FileCollection Collection { get; private set; }

		static void Main(string[] args)
		{
			Directory.CreateDirectory("do_resources");

			var resourceXml = new WebClient().DownloadString("https://darkorbit-22.bpsecure.com/spacemap/xml/resources.xml");
			Collection = XmlToT<FileCollection>(resourceXml);
			Parallel.ForEach(Collection.Files, new ParallelOptions {MaxDegreeOfParallelism = 10 }, DownloadFile);

			Console.WriteLine("Done!");
			Console.ReadLine();
		}

		private static void DownloadFile(File file)
		{
			var location = Collection.Locations.Find(loc => loc.Id == file.Location).Path;
			Directory.CreateDirectory($"do_resources/{location}");
			Console.WriteLine($"Downloading {location}{file.Name}.{file.Type}");

			var url = $"https://darkorbit-22.bpsecure.com/spacemap/{location}{file.Name}.{file.Type}";

			using (var sr = new StreamReader(HttpWebRequest.Create(url).GetResponse().GetResponseStream()))
			using (var sw = new StreamWriter($"do_resources/{location}{file.Name}.{file.Type}"))
			{
				sw.Write(sr.ReadToEnd());
			}
		}

		private static T XmlToT<T>(string xml)
		{
			XmlSerializer serializer = new XmlSerializer(typeof(T));
			using (TextReader reader = new StringReader(xml))
			{
				return (T)serializer.Deserialize(reader);
			}
		}
	}
}
