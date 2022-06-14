
/*   Copyright 2019-2022 Cinegy GmbH

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CommandLine;
using static System.String;
using System.Runtime;

namespace Cinegy.TsMuxer
{
    /// <summary>
    /// This tool was created to allow testing of subtitle PIDs being muxed into a Cinegy TS output
    /// 
    /// Don't forget this EXE will need inbound firewall traffic allowed inbound - since multicast appears as inbound traffic...
    /// 
    /// Originally created by Lewis, so direct complaints his way.
    /// </summary>
    public class Program
    {

        private enum ExitCodes
        {
            SubPidError = 102,
            UnknownError = 2000
        }

        private static UdpClient _outputUdpClient;

        private static bool _mainPacketsStarted;
        private static bool _subPacketsStarted;
        private static bool _pendingExit;
        private static bool _suppressOutput;

        private static StreamOptions _options;
        private static readonly object ConsoleOutputLock = new object();

        private static SubPidMuxer _muxer;
        
        private static int Main(string[] args)
        {
            try
            {
                var result = Parser.Default.ParseArguments<StreamOptions>(args);

                return result.MapResult(
                    Run,
                    _ => CheckArgumentErrors());
            }
            catch (Exception ex)
            {
                Environment.ExitCode = (int)ExitCodes.UnknownError;
                PrintToConsole("Unknown error: " + ex.Message);
                throw;
            }
        }

        private static int CheckArgumentErrors()
        {
            //will print using library the appropriate help - now pause the console for the viewer
            Console.WriteLine("Hit enter to quit");
            Console.ReadLine();
            return -1;
        }

        ~Program()
        {
            Console.CursorVisible = true;
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Console.CursorVisible = true;
            if (_pendingExit) return; //already trying to exit - allow normal behaviour on subsequent presses
            _pendingExit = true;
            e.Cancel = true;
        }

        private static int Run(StreamOptions options)
        {
            Console.Clear();
            Console.CancelKeyPress += Console_CancelKeyPress;
            
            Console.WriteLine(
               // ReSharper disable once AssignNullToNotNullAttribute
               $"Cinegy TS Muxing tool (Built: {File.GetCreationTime(AppContext.BaseDirectory)})\n");

            _options = options;

            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            var subPids = new List<int>();

            foreach(var pid in _options.SubPids.Split(',')) { if (int.TryParse(pid, out var intPid)) subPids.Add(intPid); }

            if (subPids.Count < 1)
            {
                Console.WriteLine("Provided sub PIDs argument did not contain one or more comma separated numbers - please check format");
                return (int)ExitCodes.SubPidError;
            }

            _suppressOutput = _options.SuppressOutput; //only suppresses extra logging to screen, not dynamic output

            _muxer = new SubPidMuxer(subPids) {PrintErrorsToConsole = !_suppressOutput};

            _muxer.PacketReady += _muxer_PacketReady;

            Console.WriteLine($"Outputting multicast data to {_options.OutputMulticastAddress}:{_options.OutputMulticastPort} via adapter {_options.MulticastAdapterAddress}");
            _outputUdpClient = PrepareOutputClient(_options.OutputMulticastAddress,_options.OutputMulticastPort,_options.MulticastAdapterAddress);
            Console.WriteLine($"Listening for Primary Transport Stream on rtp://@{ _options.MainMulticastAddress}:{ _options.MainMulticastPort}");
            StartListeningToPrimaryStream();
            Console.WriteLine($"Listening for Sub Transport Stream on rtp://@{_options.SubMulticastAddress}:{_options.SubMulticastPort}");
            StartListeningToSubStream();
            
            Console.CursorVisible = false;
            
            Thread.Sleep(40);
            while (!_pendingExit)
            {
                lock (ConsoleOutputLock)
                {
                    Console.SetCursorPosition(0, 8);
                    Console.WriteLine($"Primary Stream Buffer fullness: {_muxer.PrimaryBufferFullness} \b \t\t\t");
                    Console.WriteLine($"Sub Stream Buffer fullness:     {_muxer.SecondaryBufferFullness} \b \t\t\t");
                    Console.WriteLine($"Sub Stream PID queue depth:     {_muxer.SecondaryPidBufferFullness} \b \t\t");
                }

                Thread.Sleep(40);
            }

            Console.CursorVisible = true;

            return 0;

        }

        private static void _muxer_PacketReady(object sender, SubPidMuxer.PacketReadyEventArgs e)
        {
            _outputUdpClient.Send(e.UdpPacketData, e.UdpPacketData.Length);
        }

        private static void StartListeningToPrimaryStream()
        {
            var listenAddress = IsNullOrEmpty(_options.MulticastAdapterAddress) ? IPAddress.Any : IPAddress.Parse(_options.MulticastAdapterAddress);

            var localEp = new IPEndPoint(listenAddress, _options.MainMulticastPort);

            var udpClient = SetupInputUdpClient(localEp, _options.MainMulticastAddress, listenAddress);
            
            var ts = new ThreadStart(delegate
            {
                PrimaryReceivingNetworkWorkerThread(udpClient, localEp);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();

        }
        
        private static void StartListeningToSubStream()
        {
            var listenAddress = IsNullOrEmpty(_options.MulticastAdapterAddress) ? IPAddress.Any : IPAddress.Parse(_options.MulticastAdapterAddress);

            var localEp = new IPEndPoint(listenAddress, _options.SubMulticastPort);

            var udpClient = SetupInputUdpClient(localEp, _options.SubMulticastAddress, listenAddress);

            var ts = new ThreadStart(delegate
            {
                SubReceivingNetworkWorkerThread(udpClient, localEp);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();

        }
        
        private static UdpClient SetupInputUdpClient(EndPoint localEndpoint, string multicastAddress, IPAddress multicastAdapter)
        {
            var udpClient = new UdpClient { ExclusiveAddressUse = false };

            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.ReceiveBufferSize = 1500 * 3000;
            udpClient.ExclusiveAddressUse = false;
            udpClient.Client.Bind(localEndpoint);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            udpClient.JoinMulticastGroup(parsedMcastAddr, multicastAdapter);

            return udpClient;
        }

        private static UdpClient PrepareOutputClient(string multicastAddress, int multicastPort, string outputAdapter)
        {
            var outputIp = outputAdapter != null ? IPAddress.Parse(outputAdapter) : IPAddress.Any;

            var outputUdpClient = new UdpClient { ExclusiveAddressUse = false };
            var localEp = new IPEndPoint(outputIp, multicastPort);

            outputUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            outputUdpClient.Ttl = (short)_options.OutputMulticastTtl;
            outputUdpClient.ExclusiveAddressUse = false;
            outputUdpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            outputUdpClient.Connect(parsedMcastAddr, multicastPort);

            return outputUdpClient;
        }

        private static void PrimaryReceivingNetworkWorkerThread(UdpClient client, IPEndPoint localEp)
        {
            while (!_pendingExit)
            {
                var data = client.Receive(ref localEp);

                if (!_mainPacketsStarted)
                {
                    PrintToConsole("Started receiving primary multicast packets...");
                    _mainPacketsStarted = true;
                }
                try
                {
                    _muxer.AddToPrimaryBuffer(ref data);
                }
                catch (Exception ex)
                {
                    PrintToConsole($@"Unhandled exception within network receiver: {ex.Message}");
                    return;
                }
            }
        }

        private static void SubReceivingNetworkWorkerThread(UdpClient client, IPEndPoint localEp)
        {
            while (!_pendingExit)
            {
                var data = client.Receive(ref localEp);

                if (!_subPacketsStarted)
                {
                    PrintToConsole("Started receiving sub multicast packets...");
                    _subPacketsStarted = true;
                }

                try
                {
                    _muxer.AddToSecondaryBuffer(ref data); 
                }
                catch (Exception ex)
                {
                    PrintToConsole($@"Unhandled exception within network receiver: {ex.Message}");
                    return;
                }
            }
        }

        private static void PrintToConsole(string message)
        {
            if (_suppressOutput)
                return;

            var currentLine = Console.CursorTop;
            Console.SetCursorPosition(0, 13);
            Console.WriteLine($" \b \b \b{message} \b \b \b \b");
            Console.SetCursorPosition(0, currentLine);
        
        }

    }

}
