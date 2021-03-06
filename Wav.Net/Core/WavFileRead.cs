﻿/*
 * Wav.Net. A .Net 2.0 based library for transcoding ".wav" (wave) files.
 * Copyright © 2014, ArcticEcho.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */



using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WavDotNet.Core
{
    public class WavFileRead<T> : WavFile, IDisposable, IEnumerable<SampleReader<T>>
    {
        private readonly string filePath;
        private bool disposed;
        private uint headerSize;
        private uint speakerMask;
        private readonly Stream stream;

        public uint? BufferCapacity { get; private set; }
        public Dictionary<ChannelPositions, SampleReader<T>> AudioData { get; private set; }
        public ushort ChannelCount { get; private set; }

        public SampleReader<T> this[ChannelPositions channel]
        {
            get
            {
                if (!AudioData.ContainsKey(channel))
                {
                    throw new KeyNotFoundException();
                }

                return AudioData[channel];
            }
        }



        # region Constructors/destructor.

        public WavFileRead(string filePath)
        {
            if (String.IsNullOrEmpty(filePath)) { throw new ArgumentException("Can not be null or empty.", "filePath"); }
            if (!File.Exists(filePath)) { throw new FileNotFoundException(); }
            if (new FileInfo(filePath).Length > int.MaxValue) { throw new ArgumentException("File is too large. Must be less than 2 GiB.", "filePath"); }

            this.filePath = filePath;
            stream = File.OpenRead(filePath);
            AudioData = new Dictionary<ChannelPositions, SampleReader<T>>();

            GetMeta();
            CheckMeta();
            stream.Dispose();
            AddChannels();
        }

        public WavFileRead(string filePath, uint? bufferCapacity)
        {
            if (String.IsNullOrEmpty(filePath)) { throw new ArgumentException("Can not be null or empty.", "filePath"); }
            if (!File.Exists(filePath)) { throw new FileNotFoundException(); }
            if (new FileInfo(filePath).Length > int.MaxValue) { throw new ArgumentException("File is too large. Must be less than 2 GiB.", "filePath"); }
            if (bufferCapacity != null && bufferCapacity < 1024) { throw new ArgumentOutOfRangeException("bufferCapacity", "Must be more than 1024."); }

            this.filePath = filePath;
            stream = File.OpenRead(filePath);
            BufferCapacity = bufferCapacity;
            AudioData = new Dictionary<ChannelPositions, SampleReader<T>>();

            GetMeta();
            CheckMeta();
            stream.Dispose();
            AddChannels();
        }

        public WavFileRead(Stream stream)
        {
            if (stream == null) { throw new ArgumentNullException("stream"); }
            if (stream.Length > int.MaxValue) { throw new ArgumentException("Stream is too large. Must be less than 2GiB.", "stream"); }

            this.stream = stream;
            AudioData = new Dictionary<ChannelPositions, SampleReader<T>>();

            GetMeta();
            CheckMeta();
            AddChannels();
        }

        public WavFileRead(Stream stream, uint? bufferCapacity)
        {
            if (stream == null) { throw new ArgumentNullException("stream"); }
            if (bufferCapacity != null && bufferCapacity < 1024) { throw new ArgumentOutOfRangeException("bufferCapacity", "Must be more than 1024."); }
            if (stream.Length > int.MaxValue) { throw new ArgumentException("Stream is too large. Must be less than 2GiB.", "stream"); }

            this.stream = stream;
            BufferCapacity = bufferCapacity;
            AudioData = new Dictionary<ChannelPositions, SampleReader<T>>();

            GetMeta();
            CheckMeta();
            AddChannels();
        }

        ~WavFileRead()
        {
            if (!disposed)
            {
                Dispose();
            }
        }

        # endregion



        public SampleReader<T> GetChannel(ChannelPositions channel)
        {
            foreach (var ch in AudioData)
            {
                if (ch.Key == channel)
                {
                    return ch.Value;
                }
            }

            throw new KeyNotFoundException("The file does not contain channel: " + channel);
        }

        public bool ChannelExists(ChannelPositions channel)
        {
            return AudioData.ContainsKey(channel);
        }

        public void Dispose()
        {
            if (disposed) { return; }

            foreach (var reader in AudioData.Values)
            {
                reader.Dispose();
            }

            GC.SuppressFinalize(this);
            disposed = true;
        }

        public IEnumerator<SampleReader<T>> GetEnumerator()
        {
            foreach (var ch in AudioData)
            {
                yield return ch.Value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }



        private void GetMeta()
        {
            var cid = new byte[4];         // Chunk ID data.
            var sc1SData = new byte[4];    // Sub-chunk 1 size data.
            var afData = new byte[2];      // Audio format data.
            var guidAfData = new byte[16]; // GUID + Audio Format data.
            var chData = new byte[2];      // Channel count data.
            var srData = new byte[4];      // Sample rate data.
            var bpsData = new byte[2];     // Bits per sample data.
            var rbdData = new byte[2];     // Real bit depth.
            var cmData = new byte[4];      // Channel mask data.
            var bytes = new byte[1024];    // Header bytes.

            // Read header bytes.
            stream.Position = 0;
            stream.Read(bytes, 0, 1024);
            var header = Encoding.ASCII.GetString(bytes);

            // Read chunk ID.
            Buffer.BlockCopy(bytes, 0, cid, 0, 4);

            if (!header.StartsWith("RIFF", StringComparison.Ordinal)) { throw new UnrecognisedWavFileException("Stream is not in a recognised wav format."); }

            // Find where the "fmt " sub-chunk starts.
            var fmtStartIndex = header.IndexOf("fmt ", StringComparison.Ordinal) + 4;

            // Read sub-chunk 1 size.
            Buffer.BlockCopy(bytes, fmtStartIndex, sc1SData, 0, 4);

            // Read audio format.
            Buffer.BlockCopy(bytes, fmtStartIndex + 4, afData, 0, 2);

            // Read channel count.
            Buffer.BlockCopy(bytes, fmtStartIndex + 6, chData, 0, 2);

            // Read sample rate.
            Buffer.BlockCopy(bytes, fmtStartIndex + 8, srData, 0, 2);

            // Read bit depth.
            Buffer.BlockCopy(bytes, fmtStartIndex + 18, bpsData, 0, 2);

            // Check if sub-chunk extension exists.
            if (BitConverter.ToUInt16(afData, 0) == 65534)
            {
                var extraSize = new byte[2];

                // Read size of extra data.
                Buffer.BlockCopy(bytes, fmtStartIndex + 20, extraSize, 0, 2);

                // Read guid/format data.
                Buffer.BlockCopy(bytes, fmtStartIndex + 28, guidAfData, 0, 16);

                // Check if sub-chunk extension is the correct size and contains vaild info. If not, it probably contains some other type of custom extension.
                if (BitConverter.ToUInt16(extraSize, 0) == 22 && (guidAfData[3] == 3 || guidAfData[3] == 1))
                {
                    // Read real bits per sample.
                    Buffer.BlockCopy(bytes, fmtStartIndex + 22, rbdData, 0, 2);

                    // Read speaker mask.
                    Buffer.BlockCopy(bytes, fmtStartIndex + 24, cmData, 0, 4);
                }
            }
            
            if (rbdData[0] == 0 && rbdData[1] == 0)
            {
                // Real bit depth not specified, assume real bit depth is same as bit depth.
                rbdData[0] = bpsData[0];
                rbdData[1] = bpsData[1];
            }

            headerSize = (uint)(header.IndexOf("data", StringComparison.Ordinal) + 8);
            Format = (WavFormat)(BitConverter.ToUInt16(afData, 0) == 65534 ? guidAfData[3] : BitConverter.ToUInt16(afData, 0));
            ChannelCount = BitConverter.ToUInt16(chData, 0);
            SampleRate = BitConverter.ToUInt32(srData, 0);
            BitDepth = BitConverter.ToUInt16(bpsData, 0);
            ValidBits = BitConverter.ToUInt16(rbdData, 0);
            speakerMask = BitConverter.ToUInt32(cmData, 0);

            AudioLengthBytes = (uint)(stream.Length - headerSize);
        }

        private void CheckMeta()
        {
            if (BitDepth == 0) { throw new UnrecognisedWavFileException("File is displaying an invalid bit depth."); }
            if (ValidBits == 0) { throw new UnrecognisedWavFileException("File is displaying an invalid real bit depth."); }
            if (SampleRate == 0) { throw new UnrecognisedWavFileException("File is displaying an invalid sample rate."); }
            if (ChannelCount == 0) { throw new UnrecognisedWavFileException("File is displaying an invalid number of channels."); }
            if (Format == WavFormat.Unknown) { throw new UnrecognisedWavFileException("Can only read audio in either PCM or IEEE format."); }
            if (BitDepth != 8 && BitDepth != 16 && BitDepth != 24 && BitDepth != 32 && BitDepth != 64 && BitDepth != 128)
            {
                throw new UnrecognisedWavFileException("File is of an unsupported bit depth of:" + BitDepth + ".\nSupported bit depths: 8, 16, 24, 32, 64 and 128.");
            }
            if (BitDepth < ValidBits)
            {
                throw new UnrecognisedWavFileException("File is displaying an invalid bit depth and/or invalid vaild bits per sample. (The file is displaying a bit depth less than its vaild bits per sample field.)");
            }
        }

        private void AddChannels()
        {
            var mask = speakerMask;

            if (speakerMask == 0)
            {
                if (ChannelCount == 1)
                {
                    SampleReader<T> reader;

                    if (filePath != null)
                    {
                        reader = BufferCapacity == null ? new SampleReader<T>(filePath, ChannelPositions.Mono) : new SampleReader<T>(filePath, ChannelPositions.Mono, BufferCapacity);
                    }
                    else
                    {
                        reader = BufferCapacity == null ? new SampleReader<T>(stream, ChannelPositions.Mono) : new SampleReader<T>(stream, ChannelPositions.Mono, BufferCapacity);
                    }

                    AudioData.Add(ChannelPositions.Mono, reader);

                    return;
                }

                mask = GetSpeakerMask(ChannelCount);
            }

            foreach (var pos in Enum.GetValues(typeof(ChannelPositions)))
            {
                var ch = (ChannelPositions)pos;

                if ((ch & (ChannelPositions)mask) != ch || ch == ChannelPositions.Mono) { continue; }

                SampleReader<T> reader;

                if (filePath != null)
                {
                    reader = BufferCapacity == null ? new SampleReader<T>(filePath, ch) : new SampleReader<T>(filePath, ch, BufferCapacity);
                }
                else
                {
                    reader = BufferCapacity == null ? new SampleReader<T>(stream, ch) : new SampleReader<T>(stream, ch, BufferCapacity);
                }

                AudioData.Add(ch, reader);
            }
        }

        private static uint GetSpeakerMask(int channelCount)
        {
            if (channelCount == 8)
            {
                return 0x33F;
            }

            uint mask = 0;
            var positions = new List<uint>();

            foreach (var pos in Enum.GetValues(typeof(ChannelPositions)))
            {
                var ch = (uint)(ChannelPositions)pos;

                if (ch != 0)
                {
                    positions.Add((uint)(ChannelPositions)pos);
                }
            }

            for (var i = 0; i < channelCount; i++)
            {
                mask += positions[i];
            }

            return mask;
        }
    }
}
