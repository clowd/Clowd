using System;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Media conversion setting
	/// </summary>
	/// <inherit>NReco.VideoConverter.OutputSettings</inherit>
	public class ConvertSettings : OutputSettings
	{
		/// <summary>
		/// Add silent audio stream to output
		/// </summary>
		public bool AppendSilentAudioStream;

		/// <summary>
		/// Seek to position (in seconds) before converting
		/// </summary>
		public float? Seek = null;

		/// <summary>
		/// Extra custom FFMpeg parameters for 'input'
		/// </summary>
		/// <remarks>
		/// FFMpeg command line arguments inserted before input file parameter (-i)
		/// </remarks>
		public string CustomInputArgs;

		public ConvertSettings()
		{
		}
	}
}