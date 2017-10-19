using CommandLine;

namespace Cinegy.TsMuxer
{
    internal class Options
    {
        [Option('l', "logfile", Required = false,
        HelpText = "Optional file to record events to.")]
        public string LogFile { get; set; }
        
        [Option('d', "descriptortags", Required = false, Default = "",
        HelpText = "Comma separated tag values added to all log entries for instance and machine identification")]
        public string DescriptorTags { get; set; }

        [Option('t', "telemetry", Required = false,
        HelpText = "Enable integrated telemetry")]

        public bool Silent { get; set; }
        [Option("silent", Required = false,
        HelpText = "Silence console output")]
        public bool SupressOutput { get; set; }

    }

    // Define a class to receive parsed values
    [Verb("mux", HelpText = "Mux from the network.")]
    internal class StreamOptions : Options
    {
        [Option('a', "multicastadapter", Required = false,
        HelpText = "IP address of the adapter to listen for multicast data (if not set, tries first binding adapter).")]
        public string MulticastAdapterAddress { get; set; }

        [Option('m', "mainmulticastaddress", Required = true,
        HelpText = "Primary multicast address to subscribe this instance to.")]
        public string MainMulticastAddress { get; set; }

        [Option('p', "mainmulticastport", Required = true,
        HelpText = "Primary multicast port number to subscribe this instance to.")]
        public int MainMulticastPort { get; set; }

        [Option('n', "submulticastaddress", Required = true,
        HelpText = "Primary multicast address to subscribe this instance to.")]
        public string SubMulticastAddress { get; set; }

        [Option('q', "submulticastport", Required = true,
        HelpText = "Primary multicast port number to subscribe this instance to.")]
        public int SubMulticastPort { get; set; }

        [Option('o', "outputmulticastaddress", Required = true,
        HelpText = "Output multicast address to send results to.")]
        public string OutputMulticastAddress { get; set; }

        [Option('r', "outputmulticastport", Required = true,
        HelpText = "Output multicast port number to send results to.")]
        public int OuputMulticastPort { get; set; }

        [Option('h', "nortpheaders", Required = false, Default = false,
        HelpText = "Optional instruction to skip the expected 12 byte RTP headers (meaning plain MPEGTS inside UDP is expected")]
        public bool NoRtpHeaders { get; set; }
        
        [Option('s', "subpids", Required = true,
        HelpText = "Comma separated list of sub stream PIDs to map into master")]
        public string SubPids { get; set; }


    }
}
