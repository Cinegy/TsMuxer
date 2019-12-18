using CommandLine;

namespace Cinegy.TsMuxer
{
    internal class Options
    {
        [Option("silent", Required = false,
        HelpText = "Silence console output")]
        public bool SuppressOutput { get; set; }

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
        public int OutputMulticastPort { get; set; }

        [Option('t', "outputmulticastttl", Required = false, Default = 1,
        HelpText = "Output multicast time-to-live (router hops)")]
        public int OutputMulticastTtl { get; set; }

        [Option('h', "nortpheaders", Required = false, Default = false,
        HelpText = "Optional instruction to skip the expected 12 byte RTP headers (meaning plain MPEGTS inside UDP is expected")]
        public bool NoRtpHeaders { get; set; }
        
        [Option('s', "subpids", Required = true,
        HelpText = "Comma separated list of sub stream PIDs to map into master")]
        public string SubPids { get; set; }


    }
}
