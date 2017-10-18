
/*   Copyright 2017 Cinegy GmbH

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
using System.Reflection;
using System.Threading;
using CommandLine;
using static System.String;
using System.Runtime;
using Cinegy.TsDecoder.Buffers;
using Cinegy.TsDecoder.TransportStream;
using System.Diagnostics;
using System.Collections.Concurrent;

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

        private static UdpClient _mainInputUdpClient;
        private static UdpClient _subInputUdpClient;
        private static UdpClient _outputUdpClient;

        private static bool _mainPacketsStarted;
        private static bool _subPacketsStarted;
        private static bool _pendingExit;
        private static List<int> _subPids = new List<int>();
        private static bool _suppressOutput;

        private static ulong _referencePcr;
        private static ulong _referenceTime;
        private static ulong _lastPcr;
        private static readonly TsPacketFactory Factory = new TsPacketFactory();

        private const int TsPacketSize = 188;
        private const short SyncByte = 0x47;

        private static StreamOptions _options;
        private static RingBuffer _ringBuffer = new RingBuffer(1000);
        private static RingBuffer _subRingBuffer = new RingBuffer(1000);
        private static RingBuffer _subPidBuffer = new RingBuffer(1000, TsPacketSize);

        private static int Main(string[] args)
        {
            try
            {
                var result = Parser.Default.ParseArguments<StreamOptions>(args);

                return result.MapResult(
                    Run,
                    errs => CheckArgumentErrors());
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
            Console.CancelKeyPress += Console_CancelKeyPress;
            
            Console.WriteLine(
               // ReSharper disable once AssignNullToNotNullAttribute
               $"Cinegy TS Muxing tool (Built: {File.GetCreationTime(Assembly.GetExecutingAssembly().Location)})\n");

            _options = options;

            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            foreach(var pid in _options.SubPids.Split(','))
            {
                int intPid = 0;
                if (int.TryParse(pid, out intPid))
                    _subPids.Add(intPid);
            }

            if (_subPids.Count < 1)
            {
                Console.WriteLine("Provided sub PIDs argument did not contain one or more comma separated numbers - please check format");
                return (int)ExitCodes.SubPidError;
            }

            _outputUdpClient = PrepareOutputClient(_options.OutputMulticastAddress,_options.OuputMulticastPort,_options.MulticastAdapterAddress);
            _mainInputUdpClient = StartListeningToPrimaryStream();
            _subInputUdpClient = StartListeningToSubStream();
            
            var queueThread = new Thread(ProcessQueueWorkerThread) { Priority = ThreadPriority.AboveNormal };

            queueThread.Start();

            var subQueueThread = new Thread(ProcessSubQueueWorkerThread) { Priority = ThreadPriority.AboveNormal };

            subQueueThread.Start();

            Console.CursorVisible = false;

            Thread.Sleep(40);
            while (!_pendingExit)
            {
                Console.SetCursorPosition(0, 8);                
                Console.WriteLine($"Primary Stream Buffer fullness: {_ringBuffer.BufferFullness}\t\t\t");
                Console.WriteLine($"Sub Stream Buffer fullness: {_subRingBuffer.BufferFullness}\t\t\t");
                Console.WriteLine($"Sub Stream PID queue depth: {_subPidBuffer.BufferFullness}\t\t\t");
                Thread.Sleep(40);
            }

            Console.CursorVisible = true;

            return 0;

        }

        private static UdpClient StartListeningToPrimaryStream()
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

            PrintToConsole($"Listening for Primary Transport Stream on rtp://@{ _options.MainMulticastAddress}:{ _options.MainMulticastPort}");

            return udpClient;
        }
        
        private static UdpClient StartListeningToSubStream()
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

            PrintToConsole($"Listening for Sub Transport Stream on rtp://@{_options.SubMulticastAddress}:{_options.SubMulticastPort}");

            return udpClient;
        }

        private static void ProcessQueueWorkerThread()
        {
            var dataBuffer = new byte[12 + (188 * 7)];

            while (_pendingExit != true)
            {
                try
                {
                    lock (_ringBuffer)
                    {
                        int dataSize;
                        ulong timestamp;

                        if(_ringBuffer.BufferFullness < 10)
                        {
                            Thread.Sleep(1);
                            continue;
                        }

                        var capacity = _ringBuffer.Remove(ref dataBuffer, out dataSize, out timestamp);

                        if (capacity > 0)
                        {
                            dataBuffer = new byte[capacity];
                            continue;
                        }

                        if (dataBuffer == null) continue;
                                               
                        if (_subPidBuffer.BufferFullness > 0)
                        {
                            //check buffer to see if any PIDs contain NULL PID that can be swapped...
                            var packets = Factory.GetTsPacketsFromData(dataBuffer, dataSize);
                            foreach (var packet in packets)
                            {
                                if (packet.Pid == (short)PidType.NullPid)
                                {
                                    //candidate for wiping with any data backed up for muxing in
                                    byte[] subPidPacketBuffer = new byte[TsPacketSize];
                                    int subPidDataSize = 0;
                                    ulong subPidTimeStamp = 0;
                                    
                                    //see if there is any data waiting to get switched into the mux...
                                    lock (_subPidBuffer)
                                    {
                                        if (_subPidBuffer.BufferFullness < 1) break;
                                        var subPidPacketDataReturned = _subPidBuffer.Remove(ref subPidPacketBuffer, out subPidDataSize, out subPidTimeStamp);
                                        if (subPidPacketDataReturned != 0 && subPidPacketDataReturned != TsPacketSize)
                                        {
                                            throw new InvalidDataException("Sub PID data seems to not be size of TS packet!");
                                        }
                                    }

                                if (packet.SourceBufferIndex % 188 != 0)
                                    {
                                        Debug.WriteLine("Misaligned packet");
                                    }

                                    Buffer.BlockCopy(subPidPacketBuffer, 0, dataBuffer, packet.SourceBufferIndex, TsPacketSize);
                                }
                            }
                            
                        }
                        
                        _outputUdpClient.Send(dataBuffer, dataSize);                        

                    }
                }
                catch (Exception ex)
                {
                    //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = $@"Unhandled exception within network receiver: {ex.Message}" });
                }
            }

            //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = "Stopping analysis thread due to exit request." });
        }
        
        private static void ProcessSubQueueWorkerThread()
        {
            var dataBuffer = new byte[12 + (188 * 7)];

            while (_pendingExit != true)
            {
                try
                {
                    if (_subRingBuffer.BufferFullness < 1)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    lock (_subRingBuffer)
                    {
                        if (_subRingBuffer.BufferFullness < 1)
                            continue;

                        int dataSize;
                        ulong timestamp;
                        
                        var capacity = _subRingBuffer.Remove(ref dataBuffer, out dataSize, out timestamp);

                        if (capacity > 0)
                        {
                            dataBuffer = new byte[capacity];
                            continue;
                        }

                        if (dataBuffer == null) continue;

                        //check to see if there are any specific TS packets by PIDs we want to select

                        var packets = Factory.GetTsPacketsFromData(dataBuffer,dataSize,false,true);

                        foreach(var packet in packets)
                        {
                            if(_subPids.Contains(packet.Pid))
                            {
                                //this pid is selected for mapping across... add to PID buffer to merge replacing NULL pid
                                var buffer = new byte[packet.SourceData.Length];
                                Buffer.BlockCopy(packet.SourceData, 0, buffer, 0, packet.SourceData.Length);
                                _subPidBuffer.Add(ref buffer);
                            }
                        }

                        //lock (_outputUdpClient)
                        //{
                        //    _outputUdpClient.Send(dataBuffer, dataSize);
                        //}
                    }
                }
                catch (Exception ex)
                {
                    //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = $@"Unhandled exception within network receiver: {ex.Message}" });
                }
            }

            //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = "Stopping analysis thread due to exit request." });
        }

        private static void AddDataToRingBuffer(ref byte[] data)
        {
            CheckPcr(data);

            if (_lastPcr > 0)
            {
                //add to buffer once we have a PCR, and set timestamp to the earliest playback time
                var pcrDelta = _lastPcr - _referencePcr;

                var span = new TimeSpan((long)(pcrDelta / 2.7));

                //TODO: Hardcoded to 200ms buffer time currently
                var broadcastTime = _referenceTime + (pcrDelta / 2.7) + ((TimeSpan.TicksPerSecond / 1000) * 20);

                _ringBuffer.Add(ref data, (ulong)broadcastTime);

            }
        }

        private static void CheckPcr(byte[] dataBuffer)
        {
            var tsPackets = Factory.GetTsPacketsFromData(dataBuffer);

            if (tsPackets == null)
            {
                //Logger.Log(new TelemetryLogEventInfo
                //{
                //    Level = LogLevel.Info,
                //    Key = "NullPackets",
                //    Message = "Packet recieved with no detected TS packets"
                //});
                return;
            }

            foreach (var tsPacket in tsPackets)
            {
                if (!tsPacket.AdaptationFieldExists) continue;
                if (!tsPacket.AdaptationField.PcrFlag) continue;
                if (tsPacket.AdaptationField.FieldSize < 1) continue;

                if (tsPacket.AdaptationField.DiscontinuityIndicator)
                {
                    Console.WriteLine("Adaptation field discont indicator");
                    continue;
                }

                if (_lastPcr == 0)
                {
                    _referencePcr = tsPacket.AdaptationField.Pcr;
                    _referenceTime = (ulong)(DateTime.UtcNow.Ticks);
                }

                _lastPcr = tsPacket.AdaptationField.Pcr;
            }
        }

        public static int FindSync(IList<byte> tsData, int offset)
        {
            if (tsData == null) throw new ArgumentNullException(nameof(tsData));

            //not big enough to be any kind of single TS packet
            if (tsData.Count < 188)
            {
                return -1;
            }

            try
            {
                for (var i = offset; i < tsData.Count; i++)
                {
                    //check to see if we found a sync byte
                    if (tsData[i] != SyncByte) continue;
                    if (i + 1 * TsPacketSize < tsData.Count && tsData[i + 1 * TsPacketSize] != SyncByte) continue;
                    if (i + 2 * TsPacketSize < tsData.Count && tsData[i + 2 * TsPacketSize] != SyncByte) continue;
                    if (i + 3 * TsPacketSize < tsData.Count && tsData[i + 3 * TsPacketSize] != SyncByte) continue;
                    if (i + 4 * TsPacketSize < tsData.Count && tsData[i + 4 * TsPacketSize] != SyncByte) continue;
                    // seems to be ok
                    return i;
                }
                return -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Problem in FindSync algorithm... : ", ex.Message);
                throw;
            }
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
            Console.WriteLine($"Outputting multicast data to {multicastAddress}:{multicastPort} via adapter {outputIp}");

            var outputUdpClient = new UdpClient { ExclusiveAddressUse = false };
            var localEp = new IPEndPoint(outputIp, multicastPort);

            outputUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
                if (data == null) continue;

                if (!_mainPacketsStarted)
                {
                    PrintToConsole("Started receiving primary multicast packets...");
                    _mainPacketsStarted = true;
                }
                try
                {
                    AddDataToRingBuffer(ref data);
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
                if (data == null) continue;

                if (!_subPacketsStarted)
                {
                    PrintToConsole("Started receiving sub multicast packets...");
                    _subPacketsStarted = true;
                }

                try
                {
                    _subRingBuffer.Add(ref data); 
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

            Console.WriteLine(message);
        }

    }

}
