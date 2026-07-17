using NAudio.Dsp;

namespace InfoPanel.AudioSpectrum
{
    internal class SpectrumAnalyzer
    {
        private const int FftSize = 4096;
        private const int FftExponent = 12; // 2^12 = 4096

        private readonly float[] _smoothedBands;
        private readonly float[] _peakBands;
        private float _smoothingFactor;
        private float _peakDecay;
        private float _gain;

        public int BandCount { get; }
        public float Smoothing { get => _smoothingFactor; set => _smoothingFactor = value; }
        public float PeakDecay { get => _peakDecay; set => _peakDecay = value; }
        public float Gain { get => _gain; set => _gain = value; }
        public float[] SmoothedBands => _smoothedBands;
        public float[] PeakBands => _peakBands;

        // Frequency band edges (logarithmic distribution)
        private readonly float[] _bandFrequencies;

        public SpectrumAnalyzer(int bandCount = 32, float smoothing = 0.3f, float peakDecay = 0.02f, float gain = 1.5f)
        {
            BandCount = bandCount;
            _smoothingFactor = smoothing;
            _peakDecay = peakDecay;
            _gain = gain;
            _smoothedBands = new float[bandCount];
            _peakBands = new float[bandCount];
            _bandFrequencies = GenerateLogFrequencies(bandCount);
        }

        private static float[] GenerateLogFrequencies(int bandCount)
        {
            // Generate logarithmically spaced frequency edges from 20Hz to 20kHz
            const float minFreq = 20f;
            const float maxFreq = 20000f;

            var freqs = new float[bandCount + 1];
            for (int i = 0; i <= bandCount; i++)
            {
                float t = (float)i / bandCount;
                freqs[i] = minFreq * MathF.Pow(maxFreq / minFreq, t);
            }
            return freqs;
        }

        public void ProcessSamples(float[] samples, int sampleRate)
        {
            if (samples.Length == 0)
            {
                // Decay towards zero when silent
                for (int i = 0; i < BandCount; i++)
                {
                    _smoothedBands[i] *= (1f - _smoothingFactor);
                    _peakBands[i] = MathF.Max(0, _peakBands[i] - _peakDecay * 100);
                }
                return;
            }

            // Take the last FftSize samples (or pad with zeros)
            var fftBuffer = new Complex[FftSize];
            int offset = Math.Max(0, samples.Length - FftSize);
            int count = Math.Min(samples.Length, FftSize);

            for (int i = 0; i < count; i++)
            {
                // Apply Hann window
                float window = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (count - 1)));
                fftBuffer[i + (FftSize - count)].X = samples[offset + i] * window;
                fftBuffer[i + (FftSize - count)].Y = 0;
            }

            // Perform FFT
            FastFourierTransform.FFT(true, FftExponent, fftBuffer);

            // Calculate magnitude spectrum
            float freqResolution = (float)sampleRate / FftSize;
            var magnitudes = new float[FftSize / 2];
            for (int i = 0; i < magnitudes.Length; i++)
            {
                float real = fftBuffer[i].X;
                float imag = fftBuffer[i].Y;
                magnitudes[i] = MathF.Sqrt(real * real + imag * imag);
            }

            // Group into frequency bands
            for (int band = 0; band < BandCount; band++)
            {
                float lowFreq = _bandFrequencies[band];
                float highFreq = _bandFrequencies[band + 1];

                int lowBin = Math.Max(1, (int)(lowFreq / freqResolution));
                int highBin = Math.Min(magnitudes.Length - 1, (int)(highFreq / freqResolution));

                if (highBin <= lowBin) highBin = lowBin + 1;

                // Average magnitude in this band
                float sum = 0;
                int binCount = 0;
                for (int bin = lowBin; bin <= highBin && bin < magnitudes.Length; bin++)
                {
                    sum += magnitudes[bin];
                    binCount++;
                }

                float avgMagnitude = binCount > 0 ? sum / binCount : 0;

                // Convert to dB scale, normalize to 0-100 range
                float db = 20f * MathF.Log10(MathF.Max(avgMagnitude, 1e-10f));
                // Map roughly -60dB..0dB to 0..100 (narrower range = taller bars)
                float normalized = MathF.Max(0, MathF.Min(100, (db + 60f) * (100f / 60f)));

                // Apply gain multiplier for visual fullness
                normalized = MathF.Min(100, normalized * _gain);

                // Apply a slight boost to lower frequencies for visual appeal
                float boostFactor = 1f + (1f - (float)band / BandCount) * 0.3f;
                normalized = MathF.Min(100, normalized * boostFactor);

                // Smooth
                _smoothedBands[band] = _smoothedBands[band] * (1f - _smoothingFactor) + normalized * _smoothingFactor;

                // Peak hold with decay - track raw (pre-smoothing) value so peaks shoot above bars
                if (normalized > _peakBands[band])
                {
                    _peakBands[band] = MathF.Min(100, normalized);
                }
                else
                {
                    _peakBands[band] = MathF.Max(0, _peakBands[band] - _peakDecay * 100);
                }
            }
        }
    }
}
