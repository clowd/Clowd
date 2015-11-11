using System;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Media concatenation setting
	/// </summary>
	/// <inherit>NReco.VideoConverter.OutputSettings</inherit>
	public class ConcatSettings : OutputSettings
	{
		/// <summary>
		/// Determine whether audio stream
		/// </summary>
		public bool ConcatVideoStream = true;

		/// <summary>
		/// Seek to position (in seconds) before converting
		/// </summary>
		public bool ConcatAudioStream = true;

		public ConcatSettings()
		{
		}
	}
}