using System;
using System.IO;
using Topshelf;

namespace ImageScannerService
{
	class Program
	{
		static void Main(string[] args)
		{
			string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
			string tempDirectory = Path.Combine(currentDirectory, "temp");
			string inputDirectory = Path.Combine(currentDirectory, "in");
		
			HostFactory.Run(
				hostConfig =>
				{
					hostConfig.Service<ImageProcessingService>(
						s =>
						{
							s.ConstructUsing(() => new ImageProcessingService(inputDirectory, tempDirectory));
							s.WhenStarted(serv => serv.Start());
							s.WhenStopped(serv => serv.Stop());
						});
					hostConfig.SetServiceName("Image Processing Service");
					hostConfig.StartAutomaticallyDelayed();
					hostConfig.RunAsLocalService();
				});
		}
	}
}
