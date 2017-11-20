using System;
using System.IO;
using Topshelf;


namespace ServerService
{
	class Program
	{
		static void Main(string[] args)
		{
			var currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
			var outputDirectory = Path.Combine(currentDirectory, "output");

			HostFactory.Run(
				hostConfig =>
				{
					hostConfig.Service<ImageServerService>(
						s =>
						{
							s.ConstructUsing(() => new ImageServerService(outputDirectory));
							s.WhenStarted(serv => serv.Start());
							s.WhenStopped(serv => serv.Stop());
						});
					hostConfig.SetServiceName("ImageServer");
					hostConfig.StartAutomaticallyDelayed();
					hostConfig.RunAsLocalService();
				});
		}
	}
}
