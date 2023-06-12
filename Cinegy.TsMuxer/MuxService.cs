/* Copyright 2023 Cinegy GmbH.

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

using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Cinegy.TsDecoder.Buffers;
using Cinegy.TsDecoder.Tables;
using Cinegy.TsDecoder.TransportStream;
using Cinegy.TsMuxer.SerializableModels.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SrtSharp;

namespace Cinegy.TsMuxer
{
    public class MuxService : IHost, IHostedService
    {
        private readonly ILogger<MuxService> _logger;
        private readonly AppConfig _appConfig;
        private readonly IHostApplicationLifetime _appLifetime;
        private CancellationToken _cancellationToken;
        // ReSharper disable once InconsistentNaming
        private const uint CRC32_POLYNOMIAL = (0x02608EDB << 1) | 1;

        private static readonly TsPacketFactory Factory = new();
        private readonly TsDecoder.TransportStream.TsDecoder _videoStreamDecoder = new();
        private readonly TsDecoder.TransportStream.TsDecoder _klvStreamDecoder = new();
        private ProgramMapTable _videoStreamPmt;
        private ProgramMapTable _klvStreamPmt;
        private int _asyncKlvPid = -1;
        private static readonly byte[] KlvDescriptorData = {0x4B, 0x4C, 0x56, 0x41};
        private static bool _pendingExit;
        private static readonly Meter MuxerMeter = new($"Cinegy.TsMuxer.{nameof(MuxService)}");
        private readonly Counter<long> _videoUdpPackets;
        private readonly Counter<long> _klvUdpPackets;
        private readonly Counter<long> _klvTsPackets;
        private readonly Counter<long> _outputUdpPackets;
        private readonly ObservableGauge<long> _retransmittedVideoPackets;
        private readonly ObservableGauge<long> _retransmittedKlvPackets;
        private readonly ObservableGauge<long> _lostVideoPackets;
        private readonly ObservableGauge<long> _lostKlvPackets;
        private readonly ObservableGauge<int> _videoUdpPktBufferFullness;
        private readonly ObservableGauge<int> _klvTsPktBufferFullness;
        private readonly ObservableGauge<int> _srtClientsConnected;
        private readonly ObservableGauge<double> _opcrDelay;

        private static Uri _videoSourceUri;
        private static Uri _klvSourceUri;
        private static Uri _outputSourceUri;

        private readonly RingBuffer _videoUdpPktRingBuffer = new(1000, 1500, true);
        private readonly RingBuffer _klvTsPktRingBuffer = new(1000, 188, true);

        private ulong _lastKlvPcr;
        private ulong _lastVideoOpcr;

        private static List<int> _outputSrtHandles = new(256);
        private static readonly CBytePerfMon LastVideoSrtPerfMon = new();
        private static readonly CBytePerfMon LastKlvSrtPerfMon = new();

        private const int DefaultChunk = 1316;

        public IServiceProvider Services => throw new NotImplementedException();

        #region Constructor and IHostedService

        public MuxService(ILogger<MuxService> logger, IConfiguration configuration,
            IHostApplicationLifetime appLifetime)
        {
            _logger = logger;
            _appConfig = configuration.Get<AppConfig>();
            _appLifetime = appLifetime;


            _videoUdpPackets = MuxerMeter.CreateCounter<long>("videoUdpPackets");
            _klvUdpPackets = MuxerMeter.CreateCounter<long>("klvUdpPackets");
            _klvTsPackets = MuxerMeter.CreateCounter<long>("klvTsPackets");
            _outputUdpPackets = MuxerMeter.CreateCounter<long>("outputUdpPackets");

            _retransmittedVideoPackets = MuxerMeter.CreateObservableGauge<long>("retransmittedVideoPackets", () => LastVideoSrtPerfMon?.pktRcvDropTotal ?? 0);
            _retransmittedKlvPackets = MuxerMeter.CreateObservableGauge<long>("retransmittedKlvPackets", () => LastKlvSrtPerfMon?.pktRcvDropTotal ?? 0);
            _lostVideoPackets = MuxerMeter.CreateObservableGauge<long>("lostVideoPackets", () => LastVideoSrtPerfMon?.pktRcvLossTotal ?? 0);
            _lostKlvPackets = MuxerMeter.CreateObservableGauge<long>("lostKlvPackets", () => LastKlvSrtPerfMon?.pktRcvLossTotal ?? 0);

            _videoUdpPktBufferFullness = MuxerMeter.CreateObservableGauge("videoUdpPktBufferFullness", () => _videoUdpPktRingBuffer.BufferFullness);
            _klvTsPktBufferFullness = MuxerMeter.CreateObservableGauge("klvTsPktBufferFullness", () => _klvTsPktRingBuffer.BufferFullness);

            _srtClientsConnected = MuxerMeter.CreateObservableGauge("srtClientsConnected", () => _outputSrtHandles.Count);
            _opcrDelay = MuxerMeter.CreateObservableGauge("opcrDelay", () => new TimeSpan((long)((_lastKlvPcr - _lastVideoOpcr) / 2.7)).TotalMilliseconds);

            var srtStartup = srt.srt_startup();
            _logger.LogInformation($"SRT started up with code: {srtStartup}");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Cinegy TS Muxer service activity");

            _cancellationToken = cancellationToken;

            StartWorker();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Shutting down Cinegy TS Muxer service activity");

            _pendingExit = true;

            _logger.LogInformation("Cinegy TS Muxer service stopped");

            return Task.CompletedTask;
        }

        #endregion

        public void StartWorker()
        {
            if (!ParseUris())
            {
                _logger.LogCritical("Shutting down due to URL parsing problem...");
                return;
            }

            //handle video source, depending on scheme
            switch (_videoSourceUri.Scheme)
            {
                case "udp":
                    _logger.LogInformation("Source video configured for MPEG-TS in plain UDP packets");
                    new Thread(new ThreadStart(delegate { VideoNetworkWorker(_videoSourceUri); })).Start();
                    break;
                case "rtp":
                    _logger.LogInformation("Source video configured for MPEG-TS in UDP packets with RTP headers");
                    new Thread(new ThreadStart(delegate { VideoNetworkWorker(_videoSourceUri); })).Start();
                    break;
                case "srt":
                    _logger.LogInformation("Source video configured for MPEG-TS in SRT transport mechanism");
                    new Thread(new ThreadStart(delegate { VideoSrtWorker(_videoSourceUri); })).Start();
                    break;
                default:
                    _logger.LogCritical(
                        $"Source video URL is described with unsupported scheme: {_videoSourceUri.Scheme}");
                    _appLifetime.StopApplication();
                    return;
            }

            //handle KLV source, depending on scheme
            switch (_klvSourceUri.Scheme)
            {
                case "udp":
                    _logger.LogInformation("Source KLV configured for MPEG-TS in plain UDP packets");
                    new Thread(new ThreadStart(delegate { KlvNetworkWorker(_klvSourceUri); })).Start();
                    break;
                case "rtp":
                    _logger.LogInformation("Source KLV configured for MPEG-TS in UDP packets with RTP headers");
                    new Thread(new ThreadStart(delegate { KlvNetworkWorker(_klvSourceUri); })).Start();
                    break;
                case "srt":
                    _logger.LogInformation("Source KLV configured for MPEG-TS in SRT transport mechanism");
                    new Thread(new ThreadStart(delegate { KlvSrtWorker(_klvSourceUri); })).Start();
                    break;
                default:
                    _logger.LogCritical(
                        $"Source KLV URL is described with unsupported scheme: {_klvSourceUri.Scheme}");
                    _appLifetime.StopApplication();
                    return;
            }

            //handle output source, depending on scheme
            switch (_outputSourceUri.Scheme)
            {
                case "udp":
                    _logger.LogInformation("Output configured for MPEG-TS in plain UDP packets");
                    new Thread(new ThreadStart(delegate { OutputNetworkWorker(_outputSourceUri); })).Start();
                    break;
                case "rtp":
                    _logger.LogInformation("Output configured for MPEG-TS in UDP packets with RTP headers");
                    throw new NotImplementedException("RTP Output is not yet implemented");
                    //break;
                case "srt":
                    _logger.LogInformation("Output configured for MPEG-TS in SRT transport mechanism");
                    new Thread(new ThreadStart(delegate { OutputClientSrtConnectionWorker(_outputSourceUri.Port); })).Start();
                    new Thread(OutputSrtWorker).Start();
                    break;
                default:
                    _logger.LogCritical($"Output URL is described with unsupported scheme: {_outputSourceUri.Scheme}");
                    _appLifetime.StopApplication();
                    return;
            }

            var startTime = DateTime.UtcNow;

            var lastConsoleHeartbeatMinute = -1;
            var lastDataProcessed = 0L;
            var runtimeFormatString = "{0} hours {1} mins";
            while (!_cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(60);
                if (DateTime.UtcNow.Minute == lastConsoleHeartbeatMinute) continue;

                lastConsoleHeartbeatMinute = DateTime.UtcNow.Minute;
                var run = DateTime.UtcNow.Subtract(startTime);
                var runtimeStr = string.Format(runtimeFormatString, Math.Floor(run.TotalHours), run.Minutes);

                _logger.LogInformation(
                    $"Running: {runtimeStr}, Data Processed: {(Factory.TotalDataProcessed - lastDataProcessed) / 1048576}MB");
            }
        }

        #region private implementation

        private bool ParseUris()
        {
            try
            {
                _videoSourceUri = new Uri(_appConfig.VideoSourceUrl);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(
                    $"Failed to decode video source URL parameter {_appConfig.VideoSourceUrl}: {ex.Message}");
                _appLifetime.StopApplication();
                return false;
            }

            try
            {
                _klvSourceUri = new Uri(_appConfig.KlvSourceUrl);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(
                    $"Failed to decode KLV source URL parameter {_appConfig.KlvSourceUrl}: {ex.Message}");
                _appLifetime.StopApplication();
                return false;
            }

            try
            {
                _outputSourceUri = new Uri(_appConfig.OutputUrl);
            }
            catch (Exception ex)
            {
                _logger.LogCritical($"Failed to decode output URL parameter {_appConfig.OutputUrl}: {ex.Message}");
                _appLifetime.StopApplication();
                return false;
            }

            return true;
        }

        private void VideoNetworkWorker(Uri sourceUri)
        {
            string multicastAddress = sourceUri.DnsSafeHost;
            int multicastPort = sourceUri.Port;
            string listenAdapter = sourceUri.UserInfo;
            var UdpClient = new UdpClient();

            var listenAddress = string.IsNullOrEmpty(listenAdapter) ? IPAddress.Any : IPAddress.Parse(listenAdapter);

            var localEp = new IPEndPoint(listenAddress, multicastPort);

            UdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            UdpClient.Client.ReceiveBufferSize = 1500 * 3000;
            UdpClient.ExclusiveAddressUse = false;
            UdpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            UdpClient.JoinMulticastGroup(parsedMcastAddr, listenAddress);

            _logger.LogInformation($"Requesting video input Transport Stream on {sourceUri.Scheme}://{listenAddress}@{multicastAddress}:{multicastPort}");

            IPEndPoint remoteEndPoint = null;
            var inputVideoPacketsStarted = false;

            while (!_pendingExit && !_cancellationToken.IsCancellationRequested)
            {
                var data = UdpClient.Receive(ref remoteEndPoint);

                _videoUdpPackets.Add(1);

                if (!inputVideoPacketsStarted)
                {
                    _logger.LogInformation("Started receiving input video multicast packets...");
                    inputVideoPacketsStarted = true;
                }

                if (data == null) continue;

                _videoUdpPktRingBuffer.Add(data, data.Length);
            }
        }

        private unsafe void VideoSrtWorker(Uri srtUri)
        {
            var srtAddress = srtUri.DnsSafeHost;

            if (Uri.CheckHostName(srtUri.DnsSafeHost) != UriHostNameType.IPv4)
            {
                srtAddress = Dns.GetHostEntry(srtUri.DnsSafeHost).AddressList.First().ToString();
            }

            while (!_pendingExit && !_cancellationToken.IsCancellationRequested)
            {
                var srtHandle = srt.srt_create_socket();

                var socketAddress = SocketHelper.CreateSocketAddress(srtAddress, srtUri.Port);

                var connectResult = srt.srt_connect(srtHandle, socketAddress, sizeof(sockaddr_in));
                if (connectResult != -1)
                {
                    _logger.LogInformation(
                        $"Requesting video input SRT Transport Stream on srt://@{srtAddress}:{srtUri.Port}");
                    var lastStatSec = 0;
                    var inputVideoPacketsStarted = false;

                    var buf = new byte[DefaultChunk];
                    while (!_pendingExit)
                    {
                        var stat = srt.srt_recvmsg(srtHandle, buf, DefaultChunk);

                        if (stat == srt.SRT_ERROR)
                        {
                            _logger.LogError("SRT error in video reading loop.");
                            break;
                        }

                        _videoUdpPackets.Add(1);

                        if (!inputVideoPacketsStarted)
                        {
                            _logger.LogInformation("Started receiving input video SRT packets...");
                            inputVideoPacketsStarted = true;
                        }

                        try
                        {
                            if (lastStatSec != DateTime.UtcNow.Second)
                            {
                                if (LastVideoSrtPerfMon != null)
                                {
                                    lock (LastVideoSrtPerfMon)
                                    {
                                        srt.srt_bistats(srtHandle, LastVideoSrtPerfMon, 0, 1);
                                    }
                                }
                                lastStatSec = DateTime.UtcNow.Second;
                            }
                            if(stat == 0)continue;

                            _videoUdpPktRingBuffer.Add(buf, stat);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($@"Unhandled exception within video SRT receiver: {ex.Message}");
                            break;
                        }
                    }

                    _logger.LogError("Closing video SRT Receiver");
                    srt.srt_close(srtHandle);
                }
                else
                {
                    _logger.LogWarning("SRT connection to video source failed");
                }

                _logger.LogInformation("Waiting 1 second before attempting video SRT Receiver restart...");
                Thread.Sleep(1000);
            }
        }

        private void KlvNetworkWorker(Uri sourceUri)
        {
            string multicastAddress = sourceUri.DnsSafeHost;
            int multicastPort = sourceUri.Port;
            string listenAdapter = sourceUri.UserInfo;
            var UdpClient = new UdpClient();

            var listenAddress = string.IsNullOrEmpty(listenAdapter) ? IPAddress.Any : IPAddress.Parse(listenAdapter);

            var localEp = new IPEndPoint(listenAddress, multicastPort);

            UdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            UdpClient.Client.ReceiveBufferSize = 1500 * 3000;
            UdpClient.ExclusiveAddressUse = false;
            UdpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            UdpClient.JoinMulticastGroup(parsedMcastAddr, listenAddress);

            _logger.LogInformation($"Requesting KLV input Transport Stream on {sourceUri.Scheme}://{listenAddress}@{multicastAddress}:{multicastPort}");

            IPEndPoint remoteEndPoint = null;
            var inputKlvPacketsStarted = false;

            while (!_pendingExit && !_cancellationToken.IsCancellationRequested)
            {
                var data = UdpClient.Receive(ref remoteEndPoint);

                _klvUdpPackets.Add(1);

                if (!inputKlvPacketsStarted)
                {
                    _logger.LogInformation("Started receiving input KLV multicast packets...");
                    inputKlvPacketsStarted = true;
                }

                if (data == null) continue;
                ProcessKlvData(data, data.Length);
            }
        }

        private unsafe void KlvSrtWorker(Uri srtUri)
        {
            var srtAddress = srtUri.DnsSafeHost;

            if (Uri.CheckHostName(srtUri.DnsSafeHost) != UriHostNameType.IPv4)
            {
                srtAddress = Dns.GetHostEntry(srtUri.DnsSafeHost).AddressList.First().ToString();
            }

            while (!_pendingExit)
            {
                var srtHandle = srt.srt_create_socket();

                var socketAddress = SocketHelper.CreateSocketAddress(srtAddress, srtUri.Port);

                var connectResult = srt.srt_connect(srtHandle, socketAddress, sizeof(sockaddr_in));
                if (connectResult != -1)
                {
                    _logger.LogInformation(
                        $"Requesting KLV input SRT Transport Stream on srt://@{srtAddress}:{srtUri.Port}");

                    var lastStatSec = 0;
                    var inputKlvPacketsStarted = false;

                    var buf = new byte[DefaultChunk];

                    while (!_pendingExit)
                    {
                        var stat = srt.srt_recvmsg(srtHandle, buf, DefaultChunk);

                        if (stat == srt.SRT_ERROR)
                        {
                            _logger.LogError("SRT error in KLV reading loop.");
                            break;
                        }

                        _klvUdpPackets.Add(1);

                        if (!inputKlvPacketsStarted)
                        {
                            _logger.LogInformation("Started receiving input KLV SRT packets...");
                            inputKlvPacketsStarted = true;
                        }

                        try
                        {
                            if (lastStatSec != DateTime.UtcNow.Second)
                            {
                                if (LastKlvSrtPerfMon != null)
                                {
                                    lock (LastKlvSrtPerfMon)
                                    {
                                        srt.srt_bistats(srtHandle, LastKlvSrtPerfMon, 0, 1);
                                    }
                                }

                                lastStatSec = DateTime.UtcNow.Second;
                            }
                        }
                        catch { }

                        ProcessKlvData(buf, stat);
                    }
                    _logger.LogError("Closing KLV SRT Receiver");
                    srt.srt_close(srtHandle);
                }
                else
                {
                    _logger.LogWarning("SRT connection to KLV source failed");
                }

                _logger.LogInformation("Waiting 1 second before attempting KLV SRT Receiver restart...");
                Thread.Sleep(1000);
            }
        }

        private void ProcessKlvData(byte[] data, int dataLen)
        {
            if (data == null) return;
            try
            {
                var packets = Factory.GetTsPacketsFromData(data, dataLen, true, true);

                if (packets == null) return;

                foreach (var packet in packets)
                {
                    if (_klvStreamDecoder.GetSelectedPmt() == null)
                    {
                        //todo:make more efficient
                        _klvStreamDecoder.AddPackets(packets);
                        break;
                    }
                    else
                    {
                        _klvStreamPmt ??= _klvStreamDecoder.GetSelectedPmt();
                        if (_klvStreamPmt != null)
                        {
                            //todo: MPTS problems here....
                            if (packet is { AdaptationFieldExists: true, AdaptationField.PcrFlag: true, AdaptationField.Pcr: > 0 })
                                _lastKlvPcr = packet.AdaptationField.Pcr;

                            if (_asyncKlvPid < 0)
                            {
                                foreach (var stream in _klvStreamPmt.EsStreams)
                                {
                                    foreach (var streamDescriptor in stream.Descriptors)
                                    {
                                        if (streamDescriptor.DescriptorTag != 0x5) continue;
                                        if (ByteArrayCompare(streamDescriptor.Data, KlvDescriptorData))
                                        {
                                            _asyncKlvPid = stream.ElementaryPid;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (packet.Pid != _asyncKlvPid) continue;

                    //this pid is selected for mapping across... add to PID buffer to merge replacing NULL pid
                    var buffer = ArrayPool<byte>.Shared.Rent(packet.SourceData.Length);
                    Buffer.BlockCopy(packet.SourceData, 0, buffer, 0, packet.SourceData.Length);
                    _klvTsPktRingBuffer.Add(buffer, packet.SourceData.Length);
                    _klvTsPackets.Add(1);
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($@"Unhandled exception within KLV SRT receiver: {ex.Message}");
                return;
            }
        }

        private unsafe void OutputClientSrtConnectionWorker(int srtPort)
        {
            var srtOutputSocket = srt.srt_create_socket();

            _logger.LogInformation($"Outputting to SRT clients at srt://0.0.0.0:{srtPort}");

            var socketAddress = SocketHelper.CreateSocketAddress("0.0.0.0", srtPort);
            srt.srt_bind(srtOutputSocket, socketAddress, sizeof(sockaddr_in));
            srt.srt_listen(srtOutputSocket, 5);

            var lenHnd = GCHandle.Alloc(sizeof(sockaddr_in), GCHandleType.Pinned);
            var addressLenPtr = new SWIGTYPE_p_int(lenHnd.AddrOfPinnedObject(), false);

            while (!_pendingExit)
            {
                var newSocket = srt.srt_accept(srtOutputSocket, socketAddress, addressLenPtr);

                if (newSocket == srt.SRT_ERROR)
                {
                    _logger.LogInformation($"SRT connection problem: {srt.srt_getlasterror_str()}.");
                }
                else
                {
                    _logger.LogInformation("New Output SRT client connected");
                    _outputSrtHandles.Add(newSocket);
                }

                Thread.Sleep(10);
            }

            lenHnd.Free();
        }

        private void OutputNetworkWorker(Uri outputUri)
        {
            _logger.LogInformation($"Outputting multicast at {outputUri.Scheme}://{outputUri.UserInfo}@{outputUri.DnsSafeHost}:{outputUri.Port}");
            string multicastAddress = outputUri.DnsSafeHost;
            int multicastPort = outputUri.Port;
            string outputAdapter = outputUri.UserInfo;
            
            var udpClient = PrepareOutputClient(multicastAddress,multicastPort, outputAdapter);

            var buffer = new byte[12 + 188 * 7];

            while (!_pendingExit)
            {
                if (_videoUdpPktRingBuffer.BufferFullness < 10)
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    var dataSize = GetOutputPacket(ref buffer);
                    if (dataSize == 0) continue;
                    udpClient.Send(buffer, dataSize);
                }
                catch { }
            } 
        }

        private void OutputSrtWorker()
        {
            var buffer = new byte[12 + 188 * 7];

            while (!_pendingExit)
            {
                if (_videoUdpPktRingBuffer.BufferFullness < 10)
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    var dataSize = GetOutputPacket(ref buffer);
                    if (dataSize == 0) continue;

                    var activeClients = ArrayPool<int>.Shared.Rent(256);
                    var activeClientsUpdateRequired = false;
                    var i = 0;
                    foreach (var clientSrtHandle in _outputSrtHandles)
                    {
                        var sndResult = srt.srt_send(clientSrtHandle, buffer, dataSize);
                        _outputUdpPackets.Add(1);

                        if (sndResult == srt.SRT_ERROR)
                        {
                            _logger.LogInformation($"SRT disconnection: {srt.srt_getlasterror_str()}.");
                            activeClientsUpdateRequired = true;
                            continue;
                        }

                        activeClients[i++] = clientSrtHandle;
                    }

                    //the transmit loop detected at least one dead socket, so bother to update the list of active clients from
                    //the array we are recycling as a list of known-active connections (array is oversized, and may contain trash from before!)
                    if (activeClientsUpdateRequired)
                    {
                        _outputSrtHandles = new List<int>(i);
                        for (var n = 0; n < i; n++)
                        {
                            _outputSrtHandles.Add(activeClients[n]);
                        }
                    }

                    ArrayPool<int>.Shared.Return(activeClients);
                }
                catch (Exception ex)
                {
                   _logger.LogError($"Exception in SRT output worker: {ex.Message}");
                }

            }

        }

        private static UdpClient PrepareOutputClient(string multicastAddress, int multicastPort, string outputAdapter)
        {
            var outputIp = outputAdapter != null ? IPAddress.Parse(outputAdapter) : IPAddress.Any;

            var outputUdpClient = new UdpClient { ExclusiveAddressUse = false };
            var localEp = new IPEndPoint(outputIp, multicastPort);

            outputUdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            //outputUdpClient.Ttl = (short)_options.OutputMulticastTtl;
            outputUdpClient.ExclusiveAddressUse = false;
            outputUdpClient.Client.Bind(localEp);

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            outputUdpClient.Connect(parsedMcastAddr, multicastPort);

            return outputUdpClient;
        }

        private int GetOutputPacket(ref byte[] buffer)
        {
            lock (_videoUdpPktRingBuffer)
            {
                var capacity = _videoUdpPktRingBuffer.Remove(buffer, out var dataSize, out _);

                if (capacity > 0)
                {
                    buffer = new byte[capacity];
                    _videoUdpPktRingBuffer.Remove(buffer, out dataSize, out _);
                }

                if (buffer == null) return 0;

                var packets = Factory.GetTsPacketsFromData(buffer, dataSize);

                //use decoder to register default program (muxing always happens on default program)
                if (_videoStreamDecoder.GetSelectedPmt() == null)
                {
                    _videoStreamDecoder.AddPackets(packets);
                }
                else
                {
                    if (_videoStreamPmt == null && _klvStreamPmt != null)
                    {
                        _videoStreamPmt = _videoStreamDecoder.GetSelectedPmt();
                        if (_videoStreamPmt == null) return 0;

                        var pmtSpaceNeeded = 0;
                        foreach (var esInfo in _klvStreamPmt.EsStreams)
                        {
                            if (esInfo.ElementaryPid == _asyncKlvPid)
                            {
                                pmtSpaceNeeded += esInfo.SourceData.Length;
                            }
                        }

                        if (_videoStreamPmt.SectionLength + pmtSpaceNeeded > 188 - 12)
                        {
                            throw new InvalidDataException(
                                "Cannot add to PMT - no room (packet spanned PMT not supported)");
                        }
                    }
                }

                //check for any PMT packets, and adjust them to reflect the new muxed reality...
                foreach (var packet in packets)
                {
                    //TODO: MPTS problems here...
                    if (packet is { AdaptationFieldExists: true, AdaptationField.OpcrFlag: true })
                        _lastVideoOpcr = packet.AdaptationField.Opcr;

                    if (_videoStreamPmt == null || packet.Pid != _videoStreamPmt.Pid) continue;
                    
                    //this is the PMT for the target program on the target stream - patch in the substream PID entries
                    foreach (var esInfo in _klvStreamPmt.EsStreams)
                    {
                        if (esInfo.ElementaryPid != _asyncKlvPid) continue;

                        try
                        {
                            //locate current SectionLength bytes in databuffer
                            var pos = packet.SourceBufferIndex +
                                      4; //advance to start of PMT data structure (past TS header)
                            var pointerField = buffer[pos];
                            pos += pointerField; //advance by pointer field
                            var sectionLen =
                                (short)(((buffer[pos + 2] & 0x3) << 8) + buffer[pos + 3]); //get current length

                            //increase length value by esinfo length
                            var extSectionLen = (short)(sectionLen + (short)esInfo.SourceData.Length);

                            //set back new length into databuffer                                        
                            var bytes = BitConverter.GetBytes(extSectionLen);
                            buffer[pos + 2] = (byte)((buffer[pos + 2] & 0xFC) + (byte)(bytes[1] & 0x3));
                            buffer[pos + 3] = bytes[0];

                            //copy esinfo source data to end of program block in pmt
                            Buffer.BlockCopy(esInfo.SourceData, 0, buffer,
                                packet.SourceBufferIndex + 4 + pointerField + sectionLen,
                                esInfo.SourceData.Length);

                            //correct CRC after each extension
                            var crcBytes = BitConverter.GetBytes(GenerateCrc(ref buffer, pos + 1, extSectionLen - 1));

                            buffer[packet.SourceBufferIndex + 4 + pointerField + extSectionLen] = crcBytes[3];
                            buffer[packet.SourceBufferIndex + 4 + pointerField + extSectionLen + 1] = crcBytes[2];
                            buffer[packet.SourceBufferIndex + 4 + pointerField + extSectionLen + 2] = crcBytes[1];
                            buffer[packet.SourceBufferIndex + 4 + pointerField + extSectionLen + 3] = crcBytes[0];
                        }
                        catch
                        {
                            _logger.LogWarning("Problem patching video stream PMT with descriptors from KLV stream (did FMV or KLV source unexpectedly change?)");
                            _klvStreamPmt = null;
                            _videoStreamPmt = null;
                        }
                    }
                }

                //insert any queued filtered async KLV PID packets
                if (_klvTsPktRingBuffer.BufferFullness > 0)
                {
                    foreach (var packet in packets)
                    {
                        if (packet.Pid != (short)PidType.NullPid) continue;

                        //candidate for wiping with any data queued up for muxing in
                        var asyncKlvPidPktBuf = new byte[188];

                        //see if there is any data waiting to get switched into the mux...
                        lock (_klvTsPktRingBuffer)
                        {
                            if (_klvTsPktRingBuffer.BufferFullness < 1)
                                break; //double check here because prior check was not thread safe

                            var asyncKlvPktData = _klvTsPktRingBuffer.Remove(asyncKlvPidPktBuf, out _, out _);
                            if (asyncKlvPktData != 0 && asyncKlvPktData != 188)
                            {
                                _logger.LogWarning("Async KLV packet data seems to not be size of TS packet!");
                                return 0;
                            }
                        }

                        if (packet.SourceBufferIndex % 188 != 0)
                        {
                            _logger.LogWarning("Misaligned packet");
                            return 0;
                        }

                        Buffer.BlockCopy(asyncKlvPidPktBuf, 0, buffer, packet.SourceBufferIndex, 188);
                    }
                }

                return dataSize;
            }
        }

        private static bool ByteArrayCompare(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> a2)
        {
            return a1.SequenceEqual(a2);
        }

        private static uint GenerateCrc(ref byte[] dataBuffer, int position, int length)
        {
            var endPos = position + length;
            var crc = uint.MaxValue;

            for (var i = position; i < endPos; i++)
            {
                for (var masking = 0x80; masking != 0; masking >>= 1)
                {
                    var carry = crc & 0x80000000;
                    crc <<= 1;
                    if (carry != 0 ^ (dataBuffer[i] & masking) != 0)
                        crc ^= CRC32_POLYNOMIAL;
                }
            }

            return crc;
        }

        #endregion

        #region IDispose

        public void Dispose()
        {
            _pendingExit = true;

            //if (_srtHandle != 0)
            //{
            //    srt.srt_close(_srtHandle);
            //}

            srt.srt_cleanup();
        }

        #endregion
    }
}