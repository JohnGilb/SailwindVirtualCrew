using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace SailwindVirtualCrew
{
    internal class CrewSoundPlayer : MonoBehaviour
    {
        private const string Phase = "Sound";

        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private AudioSource _source;

        internal static CrewSoundPlayer Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            _source = gameObject.AddComponent<AudioSource>();
            _source.spatialBlend = 0f;
            _source.volume = 1f;

            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string soundsDir = Path.Combine(dllDir, "Sounds");

            if (!Directory.Exists(soundsDir))
            {
                Directory.CreateDirectory(soundsDir);
                CrewDebugLog.Ok(Phase, "Created sounds directory: " + soundsDir);
                return;
            }

            foreach (string file in Directory.GetFiles(soundsDir, "*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var clip = LoadWav(file);
                    if (clip != null)
                    {
                        clip.name = name;
                        _clips[name] = clip;
                        CrewDebugLog.Ok(Phase, "Loaded sound '" + name + "'");
                    }
                }
                catch (Exception e)
                {
                    CrewDebugLog.Warn(Phase, "Failed to load '" + name + "': " + e.Message);
                }
            }
        }

        internal void Play(string name)
        {
            if (_clips.TryGetValue(name, out var clip))
                _source.PlayOneShot(clip);
            else
                CrewDebugLog.Warn(Phase, "Sound '" + name + "' not found.");
        }

        private static AudioClip LoadWav(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                throw new Exception("Not a valid WAV file.");

            int pos = 12;
            int channels = 0, sampleRate = 0, bitsPerSample = 0;
            int dataOffset = 0, dataLength = 0;

            while (pos < bytes.Length - 8)
            {
                string tag = Encoding.ASCII.GetString(bytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
                pos += 8;

                if (tag == "fmt ")
                {
                    // audio format (must be 1 = PCM)
                    int fmt = BitConverter.ToInt16(bytes, pos);
                    if (fmt != 1)
                        throw new Exception("Only PCM WAV is supported (format=" + fmt + ").");
                    channels     = BitConverter.ToInt16(bytes, pos + 2);
                    sampleRate   = BitConverter.ToInt32(bytes, pos + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, pos + 14);
                }
                else if (tag == "data")
                {
                    dataOffset = pos;
                    dataLength = chunkSize;
                    break;
                }

                pos += chunkSize;
            }

            if (dataOffset == 0 || channels == 0 || sampleRate == 0)
                throw new Exception("WAV is missing required chunks.");

            int bytesPerSample = bitsPerSample / 8;
            int sampleCount = dataLength / bytesPerSample;
            float[] samples = new float[sampleCount];

            if (bitsPerSample == 16)
            {
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / 32768f;
            }
            else if (bitsPerSample == 8)
            {
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = (bytes[dataOffset + i] - 128) / 128f;
            }
            else
            {
                throw new Exception("Unsupported bit depth: " + bitsPerSample);
            }

            var clip = AudioClip.Create("_", sampleCount / channels, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
