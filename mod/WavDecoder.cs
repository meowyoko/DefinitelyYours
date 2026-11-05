using System;
using System.Text;
using UnityEngine;

namespace AiPhone
{
    internal static class WavDecoder
    {
        private const int ExpectedSampleRate = 44100;

        internal static AudioClip DecodePcm16Mono44100(byte[] bytes, string clipName)
        {
            if (bytes == null || bytes.Length < 44)
            {
                throw new InvalidOperationException("WAV 数据过短");
            }

            if (ReadFourCc(bytes, 0) != "RIFF" || ReadFourCc(bytes, 8) != "WAVE")
            {
                throw new InvalidOperationException("不是 RIFF/WAVE 文件");
            }

            ushort formatTag = 0;
            ushort channels = 0;
            int sampleRate = 0;
            ushort bitsPerSample = 0;
            int dataOffset = -1;
            int dataSize = 0;

            int offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                string chunkId = ReadFourCc(bytes, offset);
                int chunkSize = ReadInt32(bytes, offset + 4);
                int chunkDataOffset = offset + 8;

                if (chunkSize < 0 || chunkDataOffset + chunkSize > bytes.Length)
                {
                    throw new InvalidOperationException("WAV 分块长度无效");
                }

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                    {
                        throw new InvalidOperationException("WAV fmt 分块过短");
                    }

                    formatTag = ReadUInt16(bytes, chunkDataOffset);
                    channels = ReadUInt16(bytes, chunkDataOffset + 2);
                    sampleRate = ReadInt32(bytes, chunkDataOffset + 4);
                    bitsPerSample = ReadUInt16(bytes, chunkDataOffset + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataOffset;
                    dataSize = chunkSize;
                }

                offset = chunkDataOffset + chunkSize + (chunkSize & 1);
            }

            if (formatTag != 1 || channels != 1 || sampleRate != ExpectedSampleRate || bitsPerSample != 16)
            {
                throw new InvalidOperationException(
                    $"不支持的 WAV：format={formatTag}, channels={channels}, rate={sampleRate}, bits={bitsPerSample}");
            }

            if (dataOffset < 0 || dataSize <= 0 || (dataSize & 1) != 0)
            {
                throw new InvalidOperationException("WAV 缺少有效的 PCM 数据");
            }

            int sampleCount = dataSize / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int sampleOffset = dataOffset + i * 2;
                short sample = (short)(bytes[sampleOffset] | bytes[sampleOffset + 1] << 8);
                samples[i] = sample / 32768f;
            }

            AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, ExpectedSampleRate, false);
            if (clip == null || !clip.SetData(samples, 0))
            {
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }

                throw new InvalidOperationException("Unity 无法创建 AudioClip");
            }

            return clip;
        }

        private static string ReadFourCc(byte[] bytes, int offset)
        {
            return Encoding.ASCII.GetString(bytes, offset, 4);
        }

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
        }

        private static int ReadInt32(byte[] bytes, int offset)
        {
            return bytes[offset]
                | bytes[offset + 1] << 8
                | bytes[offset + 2] << 16
                | bytes[offset + 3] << 24;
        }
    }
}
