using System;

namespace NReco.VideoConverter
{
	/// <summary>
	/// Represents common media format strings
	/// </summary>
	/// <remarks>
	/// Complete list of formats supported by FFMpeg: ffmpeg.exe -formats
	/// </remarks>
	public class Format
	{
		/// <summary>
		/// AC-3 - encode and decode
		/// </summary>
		public const string ac3 = "ac3";

		/// <summary>
		/// Audio IFF (AIFF) - encode and decode
		/// </summary>
		public const string aiff = "aiff";

		/// <summary>
		/// raw PCM A-law - encode and decode
		/// </summary>
		public const string alaw = "alaw";

		/// <summary>
		/// ASF - encode and decode
		/// </summary>
		public const string asf = "asf";

		/// <summary>
		/// AST (Audio format used on the Nintendo Wii.) - encode and decode
		/// </summary>
		public const string ast = "ast";

		/// <summary>
		/// Sun AU - encode and decode
		/// </summary>
		public const string au = "au";

		/// <summary>
		/// AVI (Audio Video Interleaved) - encode and decode
		/// </summary>
		public const string avi = "avi";

		/// <summary>
		/// Apple CAF (Core Audio Format) - encode and decode
		/// </summary>
		public const string caf = "caf";

		/// <summary>
		/// raw DTS
		/// </summary>
		public const string dts = "dts";

		/// <summary>
		/// raw E-AC-3 - encode and decode
		/// </summary>
		public const string eac3 = "eac3";

		/// <summary>
		/// FFM (FFserver live feed) - encode and decode
		/// </summary>
		public const string ffm = "ffm";

		/// <summary>
		/// raw FLAC - encode and decode
		/// </summary>
		public const string flac = "flac";

		/// <summary>
		/// FLV (Flash Video) - encode and decode
		/// </summary>
		public const string flv = "flv";

		public const string gif = "gif";

		/// <summary>
		/// raw H.261 - encode and decode
		/// </summary>
		public const string h261 = "h261";

		/// <summary>
		/// raw H.263 - encode and decode
		/// </summary>
		public const string h263 = "h263";

		/// <summary>
		/// raw H.264 - encode and decode
		/// </summary>
		public const string h264 = "h264";

		/// <summary>
		/// raw H.265 - only decode
		/// </summary>
		public const string h265 = "h265";

		/// <summary>
		/// Matroska - encode and decode
		/// </summary>
		public const string matroska = "matroska";

		/// <summary>
		/// raw MPEG-4 video - encode and decode
		/// </summary>
		public const string m4v = "m4v";

		/// <summary>
		/// raw MJPEG video - encode and decode
		/// </summary>
		public const string mjpeg = "mjpeg";

		/// <summary>
		/// QuickTime / MOV - encode and decode
		/// </summary>
		public const string mov = "mov";

		/// <summary>
		/// MP4 (MPEG-4 Part 14) - encode and decode
		/// </summary>
		/// <remarks>
		/// MP4 container cannot be used with live streams!
		/// </remarks>
		public const string mp4 = "mp4";

		/// <summary>
		/// MPEG-1 Systems / MPEG program stream - encode and decode
		/// </summary>
		public const string mpeg = "mpeg";

		/// <summary>
		/// PCM mu-law - encode and decode
		/// </summary>
		public const string mulaw = "mulaw";

		/// <summary>
		/// Ogg - encode and decode
		/// </summary>
		public const string ogg = "ogg";

		/// <summary>
		/// Sony OpenMG audio - encode and decode
		/// </summary>
		public const string oma = "oma";

		/// <summary>
		/// WebM - encode and decode
		/// </summary>
		public const string webm = "webm";

		public const string raw_data = "data";

		public const string raw_video = "rawvideo";

		public const string rm = "rm";

		public const string swf = "swf";

		public Format()
		{
		}
	}
}