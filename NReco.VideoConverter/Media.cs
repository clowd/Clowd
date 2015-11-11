using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NReco.VideoConverter
{
	internal class Media
	{
		public Stream DataStream
		{
			get;
			set;
		}

		public string Filename
		{
			get;
			set;
		}

		public string Format
		{
			get;
			set;
		}

		public Media()
		{
		}
	}
}