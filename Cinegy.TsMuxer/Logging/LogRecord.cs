using System;
using System.Diagnostics;
using System.Runtime.Serialization;

namespace Cinegy.TsMuxer.Logging
{
    [DataContract]
    internal class LogRecord
    {
        [DataMember]
        public string EventTime => DateTime.UtcNow.ToString("o");

        [DataMember]
        public string EventMessage { get; set; }

        [DataMember]
        public string EventCategory { get; set; }

        [DataMember]
        public string ProductName => "TSMuxer";

        [DataMember]
        public string ProductVersion
            => FileVersionInfo.GetVersionInfo(AppContext.BaseDirectory).ProductVersion;

        [DataMember]
        public string EventKey { get; set; }

        [DataMember]
        public string EventTags { get; set; }
        
    }
}
