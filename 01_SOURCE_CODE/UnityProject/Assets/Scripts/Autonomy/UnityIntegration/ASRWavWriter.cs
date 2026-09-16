using System;
using System.IO;
using UnityEngine;

namespace Autonomy.UnityIntegration
{
    internal static class ASRWavWriter
    {
        public static string Write(string directory, string fileNameWithoutExtension, float[] samples, int channels, int sampleRate)
        {
            if (samples == null || samples.Length == 0)
            {
                return string.Empty;
            }

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{Sanitize(fileNameWithoutExtension)}.wav");

            using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
            using BinaryWriter writer = new(fileStream);

            int byteRate = sampleRate * channels * 2;
            int dataSize = samples.Length * 2;

            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                writer.Write((short)Mathf.RoundToInt(clamped * short.MaxValue));
            }

            return path;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "asr_capture";
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value.Replace(' ', '_');
        }
    }
}
