using System.Collections.Generic;

namespace QueueData
{
	public class DataChunk
	{
		public List<byte> Buffer { get; set; }
		public int BufferSize { get; set; }
		public int Size { get; set; }
		public int Position { get; set; }
	}
}
