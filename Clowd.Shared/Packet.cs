using RT.Util.ExtensionMethods;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Clowd.Shared
{
    public class Packet
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Packet"/> class.
        /// </summary>
        public Packet()
        {
            this.Headers = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            this.PayloadBytes = new byte[0];
        }
        /// <summary>
        /// Initializes a new instance of the <see cref="Packet"/> class.
        /// </summary>
        /// <param name="command">The Command.</param>
        public Packet(string command)
            :this()
        {
            this.Command = command;
        }
        /// <summary>
        /// Initializes a new instance of the <see cref="Packet"/> class. This is for when reconstructing packet structure from network stream.
        /// </summary>
        /// <param name="cmd">
        /// The Command.
        /// </param>
        /// <param name="headers">
        /// The Header dictionary.
        /// </param>
        /// <param name="payload">
        /// The Payload byte array.
        /// </param>
        public Packet(string cmd, SortedDictionary<string, string> headers, byte[] payload)
        {
            this.Command = cmd;

            if (headers != null)
                // Checks for InvariantCultureIgnoreCase, if the dictionary was incorrectly initalized, create a new one.
                this.Headers = (headers.Comparer == StringComparer.InvariantCultureIgnoreCase)
                                   ? headers
                                   : new SortedDictionary<string, string>(headers, StringComparer.InvariantCultureIgnoreCase);
            else
                this.Headers = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            this.PayloadBytes = payload;
        }

        /// <summary>
        /// Gets or sets the command which determines what this packet will do, and the Headers it is required to have.
        /// </summary>
        public string Command { get; set; }
        public byte[] PayloadBytes { get; set; }
        public string Payload
        {
            get
            {
                return this.PayloadBytes.FromUtf8();
            }
            set
            {
                this.PayloadBytes = value.ToUtf8();
            }
        }
        public readonly SortedDictionary<string, string> Headers;
        public bool HasPayload
        {
            get
            {
                return PayloadBytes.Any();
            }
        }

        /// <summary>
        /// Serializes the class into a byte array that can be written to a network stream and deserialized on the other side.
        /// </summary>
        /// <returns>
        /// Byte array to be written to NetworkStream
        /// </returns>
        public byte[] Serialize()
        {
            var output = new StringBuilder();
            if (this.PayloadBytes.Any())
            {
                var len = this.PayloadBytes.Count().ToString(CultureInfo.InvariantCulture);
                if (this.Headers.ContainsKey("CONTENT-LENGTH"))
                {
                    this.Headers["CONTENT-LENGTH"] = len;
                }
                else
                {
                    this.Headers.Add("CONTENT-LENGTH", len);
                }
            }

            output.Append(this.Command + "\n");

            foreach (var header in this.Headers.Where(header => !String.IsNullOrEmpty(header.Value)))
            {
                output.Append(String.Format("{0}: {1}\n", header.Key.ToUpperInvariant(), header.Value));
            }

            output.Append("\n");

            byte[] top = Encoding.ASCII.GetBytes(output.ToString());
            int topCount = top.Count();
            int payloadCount = this.PayloadBytes.Count();
            byte[] all = new byte[topCount + payloadCount];
            Array.Copy(top, all, topCount);
            Array.Copy(this.PayloadBytes, 0, all, topCount, payloadCount);

            return all;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("[Packet Dump: " + this.Command + "]");
            foreach (var kvp in this.Headers)
            {
                sb.AppendLine(String.Format("---{0}: {1}", kvp.Key, kvp.Value));
            }
            if (this.PayloadBytes.Any())
            {
                sb.AppendLine("--------------------------------------------------------------------------------");
                if (this.Payload.Length > 1000)
                {
                    sb.AppendLine(this.Payload.SubstringSafe(0, 1000));
                    sb.AppendLine("(Display trunicated to 1000 bytes. Actual length: " + this.Payload.Length + " bytes.");
                }
                else
                {
                    sb.AppendLine(this.Payload);
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
            }
            return sb.ToString();
        }
    }
}
