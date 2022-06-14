using Cinegy.TsDecoder.Buffers;
using Cinegy.TsDecoder.Tables;
using Cinegy.TsDecoder.TransportStream;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Cinegy.TsMuxer
{
    internal class SubPidMuxer : IDisposable
    {

        private bool _pendingExit;

        private readonly RingBuffer _ringBuffer = new RingBuffer(1000);
        private readonly RingBuffer _subRingBuffer = new RingBuffer(1000);
        private readonly RingBuffer _subPidBuffer = new RingBuffer(1000, TsPacketSize);
        private readonly List<int> _subPids;
        private ProgramMapTable _subStreamSourcePmt;
        private ProgramMapTable _mainStreamTargetPmt;
        private ulong _referencePcr;
        private ulong _referenceTime;
        private ulong _lastPcr;

        private readonly TsDecoder.TransportStream.TsDecoder _subStreamDecoder = new TsDecoder.TransportStream.TsDecoder();
        private readonly TsDecoder.TransportStream.TsDecoder _mainStreamDecoder = new TsDecoder.TransportStream.TsDecoder();
        private static readonly TsPacketFactory Factory = new TsPacketFactory();

        // ReSharper disable once InconsistentNaming
        private const uint CRC32_POLYNOMIAL = ((0x02608EDB << 1) | 1);
        private const int TsPacketSize = 188;
        private const short SyncByte = 0x47;

        public bool PrintErrorsToConsole { get; set; }
        public int PrimaryBufferFullness => _ringBuffer.BufferFullness;
        public int SecondaryBufferFullness => _subRingBuffer.BufferFullness;

        public SubPidMuxer(List<int> subPids)
        {
            _subPids = subPids;

            var queueThread = new Thread(ProcessQueueWorkerThread) {Priority = ThreadPriority.AboveNormal};

            queueThread.Start();

            var subQueueThread = new Thread(ProcessSubQueueWorkerThread) {Priority = ThreadPriority.AboveNormal};

            subQueueThread.Start();
        }

        public int SecondaryPidBufferFullness
        {
            get
            {
                lock (_subPidBuffer)
                {
                    return _subPidBuffer.BufferFullness;
                }
            }
        }

        public void AddToPrimaryBuffer(ref byte[] data)
        {
            CheckPcr(data);

            if (_lastPcr > 0)
            {
                //add to buffer once we have a PCR, and set timestamp to the earliest playback time
                var pcrDelta = _lastPcr - _referencePcr;

                //TODO: Hardcoded to 200ms buffer time currently
                var broadcastTime = _referenceTime + (pcrDelta / 2.7) + ((TimeSpan.TicksPerSecond / 1000) * 20);

                _ringBuffer.Add(ref data, (ulong) broadcastTime);

            }
        }

        public void AddToSecondaryBuffer(ref byte[] data)
        {
            _subRingBuffer.Add(ref data);
        }

        private void ProcessQueueWorkerThread()
        {
            var dataBuffer = new byte[12 + (188 * 7)];

            while (_pendingExit != true)
            {
                try
                {
                    if (_ringBuffer.BufferFullness < 10)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    lock (_ringBuffer)
                    {
                        var capacity = _ringBuffer.Remove(ref dataBuffer, out var dataSize, out _);

                        if (capacity > 0)
                        {
                            dataBuffer = new byte[capacity];
                            continue;
                        }

                        if (dataBuffer == null) continue;

                        var packets = Factory.GetTsPacketsFromData(dataBuffer, dataSize);

                        //use decoder to register default program (muxing always happens on default program)
                        if (_mainStreamDecoder.GetSelectedPmt() == null)
                        {
                            _mainStreamDecoder.AddPackets(packets);
                        }
                        else
                        {
                            if (_mainStreamTargetPmt == null && _subStreamSourcePmt != null)
                            {
                                _mainStreamTargetPmt = _mainStreamDecoder.GetSelectedPmt();

                                var pmtSpaceNeeded = 0;
                                foreach (var esInfo in _subStreamSourcePmt.EsStreams)
                                {
                                    if (_subPids.Contains(esInfo.ElementaryPid))
                                    {
                                        pmtSpaceNeeded += esInfo.SourceData.Length;
                                    }
                                }

                                if ((_mainStreamTargetPmt.SectionLength + pmtSpaceNeeded) > (TsPacketSize - 12))
                                {
                                    throw new InvalidDataException(
                                        "Cannot add to PMT - no room (packet spanned PMT not supported)");
                                }
                            }
                        }

                        //check for any PMT packets, and adjust them to reflect the new muxed reality...
                        foreach (var packet in packets)
                        {
                            if (_mainStreamTargetPmt != null && packet.Pid == _mainStreamTargetPmt.Pid)
                            {
                                //this is the PMT for the target program on the target stream - patch in the substream PID entries
                                foreach (var esInfo in _subStreamSourcePmt.EsStreams)
                                {
                                    if (_subPids.Contains(esInfo.ElementaryPid))
                                    {
                                        //locate current SectionLength bytes in databuffer
                                        var pos = packet.SourceBufferIndex +
                                                  4; //advance to start of PMT data structure (past TS header)
                                        var pointerField = dataBuffer[pos];
                                        pos += pointerField; //advance by pointer field
                                        var sectionLength =
                                            (short) (((dataBuffer[pos + 2] & 0x3) << 8) +
                                                     dataBuffer[pos + 3]); //get current length

                                        //increase length value by esinfo length
                                        var extendedSectionLength =
                                            (short) (sectionLength + (short) esInfo.SourceData.Length);

                                        //set back new length into databuffer                                        
                                        var bytes = BitConverter.GetBytes(extendedSectionLength);
                                        dataBuffer[pos + 2] =
                                            (byte) ((dataBuffer[pos + 2] & 0xFC) + (byte) (bytes[1] & 0x3));
                                        dataBuffer[pos + 3] = bytes[0];

                                        //copy esinfo source data to end of program block in pmt
                                        Buffer.BlockCopy(esInfo.SourceData, 0, dataBuffer,
                                            packet.SourceBufferIndex + 4 + pointerField + sectionLength,
                                            esInfo.SourceData.Length);

                                        //correct CRC after each extension
                                        var crcBytes = BitConverter.GetBytes(GenerateCrc(ref dataBuffer, pos + 1,
                                            extendedSectionLength - 1));
                                        dataBuffer[packet.SourceBufferIndex + 4 + pointerField + extendedSectionLength]
                                            = crcBytes[3];
                                        dataBuffer[
                                                packet.SourceBufferIndex + 4 + pointerField + extendedSectionLength +
                                                1] =
                                            crcBytes[2];
                                        dataBuffer[
                                                packet.SourceBufferIndex + 4 + pointerField + extendedSectionLength +
                                                2] =
                                            crcBytes[1];
                                        dataBuffer[
                                                packet.SourceBufferIndex + 4 + pointerField + extendedSectionLength +
                                                3] =
                                            crcBytes[0];
                                    }
                                }
                            }
                        }

                        //insert any queued filtered sub PID packets
                        if (_subPidBuffer.BufferFullness > 0)
                        {
                            foreach (var packet in packets)
                            {
                                if (packet.Pid == (short) PidType.NullPid)
                                {
                                    //candidate for wiping with any data queued up for muxing in
                                    byte[] subPidPacketBuffer = new byte[TsPacketSize];

                                    //see if there is any data waiting to get switched into the mux...
                                    lock (_subPidBuffer)
                                    {
                                        if (_subPidBuffer.BufferFullness < 1)
                                            break; //double check here because prior check was not thread safe
                                        var subPidPacketDataReturned = _subPidBuffer.Remove(ref subPidPacketBuffer,
                                            out _, out _);
                                        if (subPidPacketDataReturned != 0 && subPidPacketDataReturned != TsPacketSize)
                                        {
                                            PrintToConsole("Sub PID data seems to not be size of TS packet!");
                                            return;
                                        }
                                    }

                                    if (packet.SourceBufferIndex % 188 != 0)
                                    {
                                        PrintToConsole("Misaligned packet");
                                        return;
                                    }

                                    Buffer.BlockCopy(subPidPacketBuffer, 0, dataBuffer, packet.SourceBufferIndex,
                                        TsPacketSize);
                                }
                            }
                        }
                        var packetReadyEventArgs = new PacketReadyEventArgs();
                        packetReadyEventArgs.UdpPacketData = new byte[dataSize];
                        Buffer.BlockCopy(dataBuffer,0,packetReadyEventArgs.UdpPacketData,0,dataSize);
                        OnPacketReady(packetReadyEventArgs);
                    }
                }
                catch (Exception ex)
                {
                    PrintToConsole($@"Unhandled exception within network receiver: {ex.Message}");
                }
            }

            //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = "Stopping analysis thread due to exit request." });
        }

        private void ProcessSubQueueWorkerThread()
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

                        var capacity = _subRingBuffer.Remove(ref dataBuffer, out dataSize, out _);

                        if (capacity > 0)
                        {
                            dataBuffer = new byte[capacity];
                            continue;
                        }

                        if (dataBuffer == null) continue;

                        //check to see if there are any specific TS packets by PIDs we want to select

                        var packets = Factory.GetTsPacketsFromData(dataBuffer, dataSize, true, true);
                        
                        if (packets == null) return;
                        
                        foreach (var packet in packets)
                        {
                            if (_subStreamDecoder.GetSelectedPmt() == null)
                            {
                                _subStreamDecoder.AddPackets(packets);
                            }
                            else
                            {
                                if (_subStreamSourcePmt == null)
                                {
                                    _subStreamSourcePmt = _subStreamDecoder.GetSelectedPmt();
                                }
                            }

                            if (_subPids.Contains(packet.Pid))
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
                    PrintToConsole($@"Unhandled exception within network receiver: {ex.Message}");
                }
            }

            //Logger.Log(new TelemetryLogEventInfo { Level = LogLevel.Info, Message = "Stopping analysis thread due to exit request." });
        }



        protected virtual void OnPacketReady(PacketReadyEventArgs e)
        {
            var handler = PacketReady;
            handler?.Invoke(this, e);
        }

        public event EventHandler<PacketReadyEventArgs> PacketReady;

        public class PacketReadyEventArgs : EventArgs
        {
            public byte[] UdpPacketData { get; set; }
        }

        private void CheckPcr(byte[] dataBuffer)
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
                    PrintToConsole("Adaptation field discont indicator");
                    continue;
                }

                if (_lastPcr == 0)
                {
                    _referencePcr = tsPacket.AdaptationField.Pcr;
                    _referenceTime = (ulong) (DateTime.UtcNow.Ticks);
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

        private static uint GenerateCrc(ref byte[] dataBuffer, int position, int length)
        {
            var endPos = position + length;
            uint crc = uint.MaxValue;

            for (int i = position; i < endPos; i++)
            {
                for (int masking = 0x80; masking != 0; masking >>= 1)
                {
                    uint carry = crc & 0x80000000;
                    crc <<= 1;
                    if (!(carry == 0) ^ !((dataBuffer[i] & masking) == 0))
                        crc ^= CRC32_POLYNOMIAL;
                }
            }

            return crc;
        }

        private void PrintToConsole(string message)
        {
            if (PrintErrorsToConsole)
            {
                var currentLine = Console.CursorTop;
                Console.SetCursorPosition(0, 13);
                Console.WriteLine($" \b \b \b{message} \b \b \b \b");
                Console.SetCursorPosition(0, currentLine);
            }
        }

    public void Dispose()
        {
            _pendingExit = true;
        }
    }
}

