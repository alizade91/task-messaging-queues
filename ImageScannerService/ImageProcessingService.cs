using System;
using System.IO;
using System.Linq;
using System.Messaging;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using QueueData;
using MigraDoc.DocumentObjectModel.Shapes;

namespace ImageScannerService
{
	class ImageProcessingService
	{
		private readonly string _inputDirecotry;
		private readonly string _tempDirectory;
		private readonly FileSystemWatcher _watcher;
		private readonly Task _processFilesTask;
		private readonly Task _sendConfigsTask;
		private readonly Task _controlTask;
		private readonly CancellationTokenSource _tokenSource;
		private readonly AutoResetEvent _newFileEvent;
		private readonly ManualResetEvent _stopWaitEvent;
		private int _timeout;
		private string _status;
		private Document _document;
		private Section _section;
		private PdfDocumentRenderer _pdfRender;

		public ImageProcessingService(string inputDirectory, string tempDirectory)
		{
			_inputDirecotry = inputDirectory;
			_tempDirectory = tempDirectory;

			if (!Directory.Exists(_inputDirecotry))
				Directory.CreateDirectory(_inputDirecotry);

			if (!Directory.Exists(_tempDirectory))
				Directory.CreateDirectory(_tempDirectory);

			if (!MessageQueue.Exists(Queues.Server))
				MessageQueue.Create(Queues.Server);

			if (!MessageQueue.Exists(Queues.Monitor))
				MessageQueue.Create(Queues.Monitor);

			if (!MessageQueue.Exists(Queues.Client))
				MessageQueue.Create(Queues.Client);

			_watcher = new FileSystemWatcher(_inputDirecotry);
			_watcher.Created += (sender, args) => _newFileEvent.Set();

			_timeout = 5000;
			_status = "Waiting";
			_stopWaitEvent = new ManualResetEvent(false);
			_tokenSource = new CancellationTokenSource();
			_newFileEvent = new AutoResetEvent(false);
			_processFilesTask = new Task(() => ProcessFiles(_tokenSource.Token));
			_sendConfigsTask = new Task(() => SendConfigs(_tokenSource.Token));
			_controlTask = new Task(() => ControlService(_tokenSource.Token));
		}

		public void ProcessFiles(CancellationToken token)
		{
			int currentImageIndex = -1;
			bool nextPageWaiting = false;
			CreateNewDocument();

			do
			{
				_status = "Process";
				foreach (var file in Directory.EnumerateFiles(_inputDirecotry).OrderBy(f => f))
				{
					string fileName = Path.GetFileName(file);
					bool isValidFormat = Regex.IsMatch(fileName, @"^img_[0-9]{3}.(jpg|png|jpeg)$");
					if (isValidFormat)
					{
						int imageIndex = GetFileIndex(fileName);
						if (imageIndex != currentImageIndex + 1 && currentImageIndex != -1 && nextPageWaiting)
						{
							SendDocument();
							CreateNewDocument();
							nextPageWaiting = false;
						}

						if (TryOpen(file, 3))
						{
							if (fileName != null)
							{
								string outFile = Path.Combine(_tempDirectory, fileName);
								if (File.Exists(outFile))
								{
									File.Delete(file);
								}
								else
								{
									File.Move(file, outFile);
								}

								AddImage(outFile);
							}
							currentImageIndex = imageIndex;
							nextPageWaiting = true;
						}
					}
					else
					{
						if (TryOpen(file, 3))
						{
							File.Delete(file);
						}
					}
				}

				_status = "Waiting";
				if (!_newFileEvent.WaitOne(_timeout) && nextPageWaiting)
				{
					SendDocument();
					CreateNewDocument();
					nextPageWaiting = false;
				}

				if (token.IsCancellationRequested)
				{
					if (nextPageWaiting)
					{
						SendDocument();
					}

					foreach (var file in Directory.EnumerateFiles(_tempDirectory))
					{
						if (TryOpen(file, 3))
						{
							File.Delete(file);
						}
					}
				}
			}
			while (!token.IsCancellationRequested);
		}

		private void CreateNewDocument()
		{
			_document = new Document();
			_section = _document.AddSection();
			_pdfRender = new PdfDocumentRenderer { Document = _document };
		}

		public void SendConfigs(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				Config configs = new Config
				{
					Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
					Status = _status,
					Timeout = _timeout
				};

				using (var serverQueue = new MessageQueue(Queues.Monitor))
				{
					Message message = new Message(configs);
					serverQueue.Send(message);
				}

				Thread.Sleep(10000);
			}
		}

		public void ControlService(CancellationToken token)
		{
			using (MessageQueue clientQueue = new MessageQueue(Queues.Client))
			{
				clientQueue.Formatter = new XmlMessageFormatter(new[] { typeof(int) });

				while (!token.IsCancellationRequested)
				{
					var asyncReceive = clientQueue.BeginPeek();

					if (WaitHandle.WaitAny(new[] { _stopWaitEvent, asyncReceive.AsyncWaitHandle }) == 0)
					{
						break;
					}

					var message = clientQueue.EndPeek(asyncReceive);
					clientQueue.Receive();
					_timeout = (int)message.Body;
				}
			}
		}

		private void SendDocument()
		{
			_pdfRender.RenderDocument();
			int pageCount = _pdfRender.PdfDocument.PageCount - 1;
			_pdfRender.PdfDocument.Pages.RemoveAt(pageCount);
			var pdfDocument = _pdfRender.PdfDocument;
			byte[] buffer = new byte[1024];

			using (MemoryStream ms = new MemoryStream())
			{
				pdfDocument.Save(ms, false);
				ms.Position = 0;
				int position = 0;
				int size = (int)Math.Ceiling((double)(ms.Length) / 1024) - 1;

				int bytesRead;
				while ((bytesRead = ms.Read(buffer, 0, buffer.Length)) > 0)
				{
					DataChunk pdfChunk = new DataChunk()
					{
						Position = position,
						Size = size,
						Buffer = buffer.ToList(),
						BufferSize = bytesRead
					};

					position++;

					using (MessageQueue serverQueue = new MessageQueue(Queues.Server, QueueAccessMode.Send))
					{
						Message message = new Message(pdfChunk);
						serverQueue.Send(message);
					}
				}
			}
		}

		private void AddImage(string file)
		{
			Image image = _section.AddImage(file);

			image.Height = _document.DefaultPageSetup.PageHeight;
			image.Width = _document.DefaultPageSetup.PageWidth;
			image.ScaleHeight = 0.7;
			image.ScaleWidth = 0.7;

			_section.AddPageBreak();
		}


		private int GetFileIndex(string fileName)
		{
			var match = Regex.Match(fileName, @"[0-9]{3}");

			return match.Success ? int.Parse(match.Value) : -1;
		}

		public void Start()
		{
			_processFilesTask.Start();
			_sendConfigsTask.Start();
			_controlTask.Start();
			_watcher.EnableRaisingEvents = true;
		}

		public void Stop()
		{
			_watcher.EnableRaisingEvents = false;
			_tokenSource.Cancel();
			_stopWaitEvent.Set();
			Task.WaitAll(_processFilesTask, _sendConfigsTask, _controlTask);
		}

		private bool TryOpen(string fileName, int tryCount)
		{
			for (int i = 0; i < tryCount; i++)
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
	}
}
