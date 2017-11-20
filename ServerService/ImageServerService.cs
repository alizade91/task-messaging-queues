using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Messaging;
using System.Threading.Tasks;
using System.Xml.Linq;
using QueueData;

namespace ServerService
{
	class ImageServerService
	{
		private readonly string _outputDirecotry;
		private int _lastTimeout;
		private readonly FileSystemWatcher _watcher;
		private readonly Task _processFilesTask;
		private readonly Task _monitoringTask;
		private readonly ManualResetEvent _stopWaitEvent;
		private readonly CancellationTokenSource _tokenSource;


		public ImageServerService(string outputDirectory)
		{
			_outputDirecotry = outputDirectory;
			if (!Directory.Exists(_outputDirecotry))
				Directory.CreateDirectory(_outputDirecotry);

			if (!MessageQueue.Exists(Queues.Server))
				MessageQueue.Create(Queues.Server);

			if (!MessageQueue.Exists(Queues.Monitor))
				MessageQueue.Create(Queues.Monitor);

			if (!MessageQueue.Exists(Queues.Client))
				MessageQueue.Create(Queues.Client);

			string pathToXml = GetFullPath();
			_watcher = new FileSystemWatcher(pathToXml) { Filter = "*.xml" };
			_watcher.Changed += Watcher_OnChanged;

			_stopWaitEvent = new ManualResetEvent(false);
			_tokenSource = new CancellationTokenSource();
			_processFilesTask = new Task(() => ProcessFiles(_tokenSource.Token));
			_monitoringTask = new Task(() => MonitorService(_tokenSource.Token));
		}

		public void ProcessFiles(CancellationToken token)
		{
			using (MessageQueue serverQueue = new MessageQueue(Queues.Server))
			{
				serverQueue.Formatter = new XmlMessageFormatter(new[] { typeof(DataChunk) });
				List<DataChunk> chunks = new List<DataChunk>();
				do
				{
					MessageEnumerator enumerator = serverQueue.GetMessageEnumerator2();
					int count = 0;

					while (enumerator.MoveNext())
					{
						object body = enumerator.Current?.Body;
						DataChunk section = body as DataChunk;
						if (section != null)
						{
							DataChunk chunk = section;
							chunks.Add(chunk);

							if (chunk.Position == chunk.Size)
							{
								SaveFile(chunks);
								chunks.Clear();
							}
						}

						count++;
					}

					for (var i = 0; i < count; i++)
					{
						serverQueue.Receive();
					}

					Thread.Sleep(1000);
				}
				while (!token.IsCancellationRequested);
			}
		}

		public void SaveFile(List<DataChunk> chunks)
		{
			int documentIndex = Directory.GetFiles(_outputDirecotry).Length + 1;
			string resultFile = Path.Combine(_outputDirecotry, $"result_{documentIndex}.pdf");
			using (Stream destination = File.Create(Path.Combine(_outputDirecotry, resultFile)))
			{
				foreach (var chunk in chunks)
				{
					destination.Write(chunk.Buffer.ToArray(), 0, chunk.BufferSize);
				}
			}
		}

		public void MonitorService(CancellationToken token)
		{
			using (MessageQueue serverQueue = new MessageQueue(Queues.Monitor))
			{
				serverQueue.Formatter = new XmlMessageFormatter(new[] { typeof(Config) });

				while (!token.IsCancellationRequested)
				{
					var asyncReceive = serverQueue.BeginPeek();

					if (WaitHandle.WaitAny(new[] { _stopWaitEvent, asyncReceive.AsyncWaitHandle }) == 0)
					{
						break;
					}

					var message = serverQueue.EndPeek(asyncReceive);
					serverQueue.Receive();
					var config = (Config)message.Body;
					_lastTimeout = config.Timeout;
					WriteConfigs(config);
				}
			}
		}

		public void WriteConfigs(Config configs)
		{
			string path = GetFullPath();
			string fullPath = Path.Combine(path, "config.csv");

			using (StreamWriter sw = File.AppendText(fullPath))
			{
				string line = $"{configs.Date},{configs.Status},{configs.Timeout}s";
				sw.WriteLine(line);
			}
		}

		private void Watcher_OnChanged(object sender, FileSystemEventArgs e)
		{
			string path = GetFullPath();
			string fullPath = Path.Combine(path, "timeout.xml");
			if (TryOpen(fullPath, 3))
			{
				XDocument document = XDocument.Load(fullPath);
				if (document.Root != null)
				{
					int timeout = int.Parse(document.Root.Value);
					if (_lastTimeout != timeout)
					{
						using (MessageQueue clientQueue = new MessageQueue(Queues.Client))
						{
							clientQueue.Send(timeout);
						}
					}
				}
			}
		}

		public void Start()
		{
			_processFilesTask.Start();
			_monitoringTask.Start();
			_watcher.EnableRaisingEvents = true;
		}

		public void Stop()
		{
			_watcher.EnableRaisingEvents = false;
			_tokenSource.Cancel();
			_stopWaitEvent.Set();
			Task.WaitAll(_processFilesTask, _monitoringTask);
		}

		private static bool TryOpen(string fileName, int tryCount)
		{
			for (var i = 0; i < tryCount; i++)
			{
				try
				{
					FileStream file = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.None);
					file.Close();

					return true;
				}
				catch (IOException)
				{
					Thread.Sleep(5000);
				}
			}

			return false;
		}

		private static string GetFullPath()
		{
			string currentDir = AppDomain.CurrentDomain.BaseDirectory;
			return Path.GetFullPath(Path.Combine(currentDir, @"..\..\"));
		}

	}
}
