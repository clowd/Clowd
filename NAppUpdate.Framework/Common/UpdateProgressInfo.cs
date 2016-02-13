using System;

namespace NAppUpdate.Framework.Common
{
	[Serializable]
	public delegate void ReportProgressDelegate(UpdateProgressInfo currentStatus);

	[Serializable]
	public class UpdateProgressInfo
	{
		public int TaskId { get; set; }
		public string TaskDescription { get; set; }

		public string Message { get; set; }
		public int Percentage { get; set; }
		public bool StillWorking { get; set; }
	}

	[Serializable]
	public class DownloadProgressInfo : UpdateProgressInfo
	{
		public long FileSizeInBytes { get; set; }
		public long DownloadedInBytes { get; set; }
	}
}
