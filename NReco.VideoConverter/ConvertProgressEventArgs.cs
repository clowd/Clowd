using System;
using System.Runtime.CompilerServices;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Provides data for ConvertProgress event
	/// </summary>
	public class ConvertProgressEventArgs : EventArgs
	{
		/// <summary>
		/// Processed media stream duration
		/// </summary>
		public TimeSpan Processed
		{
			get;
			private set;
		}

		/// <summary>
		/// Total media stream duration
		/// </summary>
		public TimeSpan TotalDuration
		{
			get;
			private set;
		}

		public ConvertProgressEventArgs(TimeSpan processed, TimeSpan totalDuration)
		{
			this.TotalDuration = totalDuration;
			this.Processed = processed;
		}
	}
}