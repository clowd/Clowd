using System;

namespace NReco.VideoConverter
{
	public class OutputSettings
	{
		/// <summary>
		/// Explicit sample rate for audio stream. Usual rates are: 44100, 22050, 11025
		/// </summary>
		public int? AudioSampleRate = null;

		/// <summary>
		/// Audio codec (complete list of audio codecs: ffmpeg -codecs)
		/// </summary>
		public string AudioCodec;

		/// <summary>
		/// Explicit video rate for video stream. Usual rates are: 30, 25
		/// </summary>
		public int? VideoFrameRate = null;

		/// <summary>
		/// Number of video frames to record
		/// </summary>
		public int? VideoFrameCount = null;

		/// <summary>
		/// Video frame size (common video sizes are listed in VideoSizes
		/// </summary>
		public string VideoFrameSize;

		/// <summary>
		/// Video codec (complete list of video codecs: ffmpeg -codecs)
		/// </summary>
		public string VideoCodec;

		/// <summary>
		/// Get or set max duration (in seconds)
		/// </summary>
		public float? MaxDuration = null;

		/// <summary>
		/// Extra custom FFMpeg parameters for 'output'
		/// </summary>
		/// <remarks>
		/// FFMpeg command line arguments inserted after input file parameter (-i) but before output file
		/// </remarks>
		public string CustomOutputArgs;

		public OutputSettings()
		{
		}

		public void SetVideoFrameSize(int width, int height)
		{
			this.VideoFrameSize = string.Format("{0}x{1}", width, height);
		}
	}
}