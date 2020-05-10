﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ColorChord.NET
{
    public static class NoteFinder
    {
        /// <summary> The buffer for audio data gathered from a system device. Circular buffer, with the current read position stored in <see cref="AudioBufferHeadRead"/>. </summary>
        public static float[] AudioBuffer = new float[8192]; // TODO: Make buffer size adjustable or auto-set based on sample rate (might be too short for super-high rates)

        /// <summary> Where in <see cref="AudioBuffer"/> we are currently reading. </summary>
        public static int AudioBufferHeadRead = 0;

        /// <summary> Where in the <see cref="AudioBuffer"/> we are currently adding new audio data. </summary>
        public static int AudioBufferHeadWrite = 0;

        /// <summary> When data was last added to the buffer. Used to detect idle state. </summary>
        public static DateTime LastDataAdd;

        /// <summary> The speed (in ms between runs) at which the note finder needs to run, set by the fastest visualizer. </summary>
        public static uint ShortestPeriod { get; private set; } = 100;

        /// <summary> The frequency at which the DFT output starts. </summary>
        private const int MinimumFrequency = 55;

        /// <summary> The frequency in Hz, that each of the raw bins from the DFT corresponds to. </summary>
        private static readonly float[] RawBinFrequencies = new float[DFTRawBinCount];

        /// <summary> How many bins compose one octave in the raw DFT data. </summary>
        public const int OctaveBinCount = 24;

        /// <summary> Over how many octaves the raw DFT data will be processed. </summary>
        public const int OctaveCount = 5;

        /// <summary> How many bins the DFT will class sound frequency data into. </summary>
        public const int DFTRawBinCount = OctaveBinCount * OctaveCount;

        // TODO: Rename/describe.
        private const int DFT_Q = 20;

        // TODO: Rename/describe.
        private const int DFT_Speedup = 1000;

        /// <summary> Determines how much the previous frame's DFT data is used in the next frame. Smooths out rapid changes from frame-to-frame, but can cause delay if too strong. </summary>
        /// <remarks> Lower values will mean less inter-frame smoothing. Range: 0.0~1.0 </remarks>
        private static float DFTIIRMultiplier = 0.65F;

        /// <summary> The non-folded frequency bins, used inter-frame to do smoothing, then folded to form the spectrum. </summary>
        /// <remarks> Re-used between cycles to do smoothing. </remarks>
        private static readonly float[] FrequencyBinValues = new float[DFTRawBinCount];

        /// <summary> Determines how much the raw DFT data is amplified before being used. </summary>
        /// <remarks> Range 0.0+ </remarks>
        private static float DFTDataAmplifier = 2F;

        /// <summary> The slope of the extra frequency-dependent amplification done to raw DFT data. Positive values increase sensitivity at higher frequencies. </summary>
        /// <remarks> Amplification is 1.0 at the minimum frequency, and 1.0 + (<see cref="DFTSensitivitySlope"/> * <see cref="DFTRawBinCount"/>) at the highest, increasing by <see cref="DFTSensitivitySlope"/> at each bin. </remarks>
        private static float DFTSensitivitySlope = 0.1F;

        /// <summary> The frequency spectrum, folded to overlap into a single octave length. </summary>
        /// <remarks> Not re-used between cycles. </remarks>
        private static readonly float[] OctaveBinValues = new float[OctaveBinCount];

        /// <summary> How often to run the octave data filter. This smoothes out each bin with adjacent ones. </summary>
        private static int OctaveFilterIterations = 2;
        
        /// <summary> How strong the octave data filter is. Higher values mean each bin is more aggresively averaged with adjacent bins. </summary>
        /// <remarks> Higher values mean less glitchy, but also less clear note peaks. Range: 0.0~1.0 </remarks>
        private static float OctaveFilterStrength = 0.5F;

        /// <summary> Up to how many note peaks can be extracted from the frequency data. </summary>
        public const int NotePeakMaxCount = OctaveBinCount / 2;

        /// <summary> The individual note distributions (peaks) detected this cycle. </summary>
        /// <remarks> Not re-used between cycles. </remarks>
        private static NoteDistribution[] NoteDistributions = new NoteDistribution[NotePeakMaxCount];

        /// <summary> The sigma value to use for <see cref="NoteDistribution"/> by default. </summary>
        private const float DefaultDistributionSigma = 1.4F;

        /// <summary> Used in normalizing all peak amplitudes. </summary>
        private const float PeakCompressCoefficient = 1F;

        /// <summary> Used in normalizing all peak amplitudes. </summary>
        private const float PeakCompressExponent = 0.5F;

        /// <summary> How close a note needs to be to a distribution peak in order to be merged. </summary>
        private static float MinNoteInfluenceDistance = 1.8F;

        // TODO: RENAME ME
        private static bool[] NotesAssociated = new bool[NotePeakMaxCount];

        // TODO: RENAME ME
        private static int[] EnduringNoteID = new int[NotePeakMaxCount];

        public static Note[] Notes = new Note[NotePeakMaxCount];

        /// <summary> How strongly the note merging filter affects the note frequency. Stronger filter means notes take longer to shift positions to move together. </summary>
        private const float NoteAttachFrequencyIIRMultiplier = 0.3F;

        /// <summary> How strongly the note merging filter affects the note amplitude. stronger filter means notes take longer to merge fully in amplitude. </summary>
        private const float NoteAttachAmplitudeIIRMultiplier = 0.35F;

        private const float NoteAttachAmplitudeIIRMultiplier2 = 0.25F;

        /// <summary> How close two existing notes need to be in order to get combined into a single note. </summary>
        /// <remarks> A distance of 2 means that a perfect A can combine with a perfect Bb etc. </remarks>
        private static float MinNoteCombineDistance = 0.5F;

        /// <summary> How large a note needs to be to not be considered dead (and therefore re-assigned). </summary>
        private const float MinNoteAmplitude = 0.001F;

        /// <summary> How large a distribution needs to be in order to be turned into a brand new note. </summary>
        private const float MinDistributionValueNewNote = 0.02F;

        /// <summary> Notes below this value get zeroed in <see cref="Note.AmplitudeFinal"/>. </summary>
        /// <remarks> Increase if low-amplitude notes are causing noise in output. </remarks>
        private const float NoteOutputChop = 0.05F;

        /// <summary> Whether to keep processing, or shut down operations.  </summary>
        private static bool KeepGoing = true;

        /// <summary> The thread doing the actual note data processing. </summary>
        private static Thread ProcessThread;

        /// <summary> Updates the sample rate if the audio source has changed. </summary>
        public static void SetSampleRate(int sampleRate)
        {
            for (int RawBinIndex = 0; RawBinIndex < DFTRawBinCount; RawBinIndex++)
            {
                RawBinFrequencies[RawBinIndex] = (float)((sampleRate / MinimumFrequency) / Math.Pow(2, (float)RawBinIndex / OctaveBinCount));
            }
        }

        /// <summary> Adjusts the note finder run interval if the newly added visualizer/output needs it to run faster, otherwise does nothing. </summary>
        /// <param name="period"> The period, in milliseconds, that you need the note finder to run at or faster than. </param>
        public static void AdjustOutputSpeed(uint period)
        {
            if (period < ShortestPeriod) { ShortestPeriod = period; }
        }

        /// <summary> Starts the processing thread. </summary>
        public static void Start()
        {
            KeepGoing = true;
            ProcessThread = new Thread(DoProcessing);
            ProcessThread.Start();
        }

        /// <summary> Stops the processing thread. </summary>
        public static void Stop()
        {
            KeepGoing = false;
            ProcessThread.Join();
        }

        /// <summary> Runs until <see cref="KeepGoing"/> becomes false, processing incoming audio data. </summary>
        private static void DoProcessing()
        {
            Stopwatch Timer = new Stopwatch();
            while (KeepGoing)
            {
                Timer.Restart();
                Cycle();
                int WaitTime = (int)(ShortestPeriod - (Timer.ElapsedMilliseconds));
                if (WaitTime > 0) { Thread.Sleep(WaitTime); }
            }
        }

        private static void Cycle()
        {
            // DFT outputs only a small number of bins, we'll need to process this data a lot to get smooth note positions.
            float[] DFTBinData = new float[DFTRawBinCount];

            DoDFTProgressive32(DFTBinData, RawBinFrequencies, DFTRawBinCount, AudioBuffer, AudioBufferHeadWrite, AudioBuffer.Length, DFT_Q, DFT_Speedup); // TODO: Use read and write heads

            // Pre-process input DFT data.
            for (int RawBinIndex = 0; RawBinIndex < DFTRawBinCount; RawBinIndex++)
            {
                float NewData = DFTBinData[RawBinIndex]; // The raw DFT data for this bin
                NewData *= DFTDataAmplifier; // Amplify incoming data by a constant
                NewData *= (1 + DFTSensitivitySlope * RawBinIndex); // Apply a frequency-dependent amplifier to increase sensitivity at higher frequencies.

                FrequencyBinValues[RawBinIndex] = (FrequencyBinValues[RawBinIndex] * DFTIIRMultiplier) + // Keep data from last frame, but reduce by a factor.
                                               (NewData * (1 - DFTIIRMultiplier)); // Add new data
            }

            // Taper off the first and last octave.
            for (int OctaveBinIndex = 0; OctaveBinIndex < OctaveBinCount; OctaveBinIndex++)
            {
                FrequencyBinValues[OctaveBinIndex] *= (OctaveBinIndex + 1F) / OctaveBinCount; // Taper the first octave
                FrequencyBinValues[DFTRawBinCount - OctaveBinIndex - 1] *= (OctaveBinIndex + 1F) / OctaveBinCount; // Taper the last octave
            }

            // Fold the bins to make one single octave-length array, where all like notes (e.g. C2, C3, C4) are combined, regardless of their original octave.
            for (int BinIndex = 0; BinIndex < OctaveBinCount; BinIndex++)
            {
                float Amplitude = 0;
                for (int Octave = 0; Octave < OctaveCount; Octave++) { Amplitude += FrequencyBinValues[(Octave * OctaveBinCount) + BinIndex]; }
                OctaveBinValues[BinIndex] = Amplitude;
            }

            // Do some filtering on the now-folded bins to remove meaningless peaks.
            // Averages out each bin a little bit with adjacent bins.
            // Runs [OctaveFilterIterations] times, averaging with strength [OctaveFilterStrength].
            float[] OctaveBinValuesPre = new float[OctaveBinCount];
            for (int Iteration = 0; Iteration < OctaveFilterIterations; Iteration++)
            {
                Array.Copy(OctaveBinValues, OctaveBinValuesPre, OctaveBinCount); // COpy the octave data into our temporary array.
                for (int BinIndex = 0; BinIndex < OctaveBinCount; BinIndex++)
                {
                    int IndexRight = (BinIndex + OctaveBinCount + 1) % OctaveBinCount; // The next bin to the right (wrapping around if needed).
                    int IndexLeft = (BinIndex + OctaveBinCount - 1) % OctaveBinCount; // The next bin to the left (wrapping around if needed).
                    float ValueRight = OctaveBinValuesPre[IndexRight];
                    float ValueLeft = OctaveBinValuesPre[IndexLeft];

                    float NewValue = OctaveBinValues[BinIndex] * (1F - OctaveFilterStrength); // Some of the current value in the bin
                    NewValue += ((ValueLeft + ValueRight) / 2) * OctaveFilterStrength; // Add the average of the adjacent bins, scaled by the filter strength

                    OctaveBinValues[BinIndex] = NewValue;
                }
            }

            // Reset all note distributions to off state.
            for (int NoteIndex = 0; NoteIndex < NotePeakMaxCount; NoteIndex++) { NoteDistributions[NoteIndex].HasNote = false; }

            // Find note distributions.
            // NOTE: This is decompose.c/DecomposeHistogram in TURBO_DECOMPOSE mode (single iteration).
            //       Non-TURBO_DECOMPOSE mode is currently not implemented here, as it doesn't seem to be used upstream.

            int DistributionsFound = 0;

            for (int BinIndex = 0; BinIndex < OctaveBinCount; BinIndex++)
            {
                int IndexLeft = (BinIndex - 1 + OctaveBinCount) % OctaveBinCount;
                int IndexRight = (BinIndex + 1) % OctaveBinCount;
                float ValueLeft = OctaveBinValues[IndexLeft];
                float ValueHere = OctaveBinValues[BinIndex];
                float ValueRight = OctaveBinValues[IndexRight];

                if (ValueLeft > ValueHere || ValueRight > ValueHere) { continue; } // Adjacent bins are higher, this is not a peak.
                if (ValueLeft == ValueHere && ValueRight == ValueHere) { continue; } // Adjacent bins are both equal, this is a plateau (e.g. all 0).

                // TODO: This isn't 100% certain.
                // This bin is a peak, adjacent values are lower.
                // Now we try to locate where the peak should be within this one bin.
                float TotalAdjacentDifference = ((ValueHere - ValueLeft) + (ValueHere - ValueRight));
                float ProportionalDifferenceLeft = (ValueHere - ValueLeft) / TotalAdjacentDifference;
                float ProportionalDifferenceRight = (ValueHere - ValueRight) / TotalAdjacentDifference;

                float InternalOffset; // Where in this bin the peak is.
                if (ProportionalDifferenceLeft < ProportionalDifferenceRight) { InternalOffset = -(0.5F - ProportionalDifferenceLeft); } // In the left half of this bin.
                else { InternalOffset = (0.5F - ProportionalDifferenceRight); } // In the right half of this bin.

                // Output the distribution information.
                NoteDistributions[DistributionsFound].Mean = BinIndex + InternalOffset;
                NoteDistributions[DistributionsFound].Amplitude = ValueHere * 4;
                NoteDistributions[DistributionsFound].Sigma = DefaultDistributionSigma;

                DistributionsFound++;
            }

            // Clear out the distributions that are not currently active.
            for (int DistrIndex = DistributionsFound; DistrIndex < NotePeakMaxCount; DistrIndex++)
            {
                NoteDistributions[DistrIndex].Mean = -1F;
                NoteDistributions[DistrIndex].Amplitude = 0F;
                NoteDistributions[DistrIndex].Sigma = DefaultDistributionSigma;
            }

            // Normalize distribution amplitudes.
            // Start by summing all peak amplitudes.
            float AmplitudeSum = 0;
            for (int DistrIndex = 0; DistrIndex < DistributionsFound; DistrIndex++) { AmplitudeSum += NoteDistributions[DistrIndex].Amplitude; }

            // Find coefficient to multiply all by.
            float AmplitudeCoefficient = (float)(PeakCompressCoefficient / Math.Pow(AmplitudeSum * PeakCompressCoefficient, PeakCompressExponent));

            // Scale peaks.
            for (int DistrIndex = 0; DistrIndex < DistributionsFound; DistrIndex++) { NoteDistributions[DistrIndex].Amplitude *= AmplitudeCoefficient; }

            // Sort peaks so they are in high-to-low amplitude order.
            Array.Sort(NoteDistributions);

            // Try to find peaks that are close together (in respect to frequency).
            // This modifies [Notes] by using new data from [NoteDistributions].
            NotesAssociated = new bool[NotePeakMaxCount]; // Whether the note in this slot has been associated to an active peak.
            int CurrentNoteID = 1;
            for (int PeakIndex = 0; PeakIndex < NotePeakMaxCount; PeakIndex++)
            {
                // For each note peak, check if a distribution is close by. If so, adjust the peak location and amplitude to use this new information.
                for (byte DistrIndex = 0; DistrIndex < DistributionsFound; DistrIndex++)
                {
                    if (!NoteDistributions[DistrIndex].HasNote && // If this distribution is not already influencing another note.
                        !NotesAssociated[PeakIndex] && // If this note is not already being influenced by another distribution
                        LoopDistance(Notes[PeakIndex].Position, NoteDistributions[DistrIndex].Mean, OctaveBinCount) < MinNoteInfluenceDistance && // The locations are close enough to merge
                        NoteDistributions[DistrIndex].Amplitude > 0.00001F) // The new data is significant
                    {
                        // note_peaks_to_dists_mapping can be implemented here if needed.
                        // TODO: I'm honestly a little bit lost as to what happens in here...
                        NoteDistributions[DistrIndex].HasNote = true; // Don't let this distribution affect other notes.
                        if (EnduringNoteID[PeakIndex] == 0) { EnduringNoteID[PeakIndex] = CurrentNoteID++; }
                        NotesAssociated[DistrIndex] = true; // This note has been influenced by a distribution, so is still active.
                        Notes[PeakIndex].Position = LoopAverageWeighted(Notes[PeakIndex].Position, (1F - NoteAttachFrequencyIIRMultiplier), NoteDistributions[DistrIndex].Mean, NoteAttachFrequencyIIRMultiplier, OctaveBinCount);

                        float NewAmplitude = Notes[PeakIndex].Amplitude;
                        NewAmplitude *= (1F - NoteAttachAmplitudeIIRMultiplier);
                        NewAmplitude += (NoteDistributions[DistrIndex].Amplitude * NoteAttachAmplitudeIIRMultiplier);
                        Notes[PeakIndex].Amplitude = NewAmplitude;
                    }
                }
            }

            // Combine notes if they are close enough.
            for (int NoteIndex1 = 0; NoteIndex1 < NotePeakMaxCount; NoteIndex1++)
            {
                for (int NoteIndex2 = 0; NoteIndex2 < NotePeakMaxCount; NoteIndex2++)
                {
                    if (NoteIndex1 == NoteIndex2) { continue; } // Don't try to compare a note with itself.

                    if (LoopDistance(Notes[NoteIndex1].Position, Notes[NoteIndex2].Position, OctaveBinCount) < MinNoteCombineDistance && // The two notes are close enough
                        Notes[NoteIndex1].Amplitude > 0 &&
                        Notes[NoteIndex2].Amplitude > 0) // Both notes need to be significant to be combined.
                    {
                        bool Is1Bigger = Notes[NoteIndex1].Amplitude > Notes[NoteIndex2].Amplitude;
                        int IndexPrimary = Is1Bigger ? NoteIndex1 : NoteIndex2; // The index of the larger note.
                        int IndexSecondary = Is1Bigger ? NoteIndex2 : NoteIndex1; // The index of the smaller note.

                        float NewPosition = LoopAverageWeighted(Notes[IndexPrimary].Position, Notes[IndexPrimary].Amplitude, Notes[IndexSecondary].Position, Notes[IndexSecondary].Amplitude, OctaveBinCount);

                        // Merge secondary into primary at new position
                        Notes[IndexPrimary].Amplitude += Notes[IndexSecondary].Amplitude;
                        Notes[IndexPrimary].Position = NewPosition;

                        // Delete secondary note
                        Notes[IndexSecondary].Amplitude = 0;
                        Notes[IndexSecondary].Position = -100;
                        EnduringNoteID[IndexSecondary] = 0;
                    }
                }
            }

            // Note slots that are empty should be assigned to not yet used distributions, if there are any.
            for (int NoteIndex = 0; NoteIndex < NotePeakMaxCount; NoteIndex++)
            {
                if (Notes[NoteIndex].Amplitude < MinNoteAmplitude)
                {
                    EnduringNoteID[NoteIndex] = 0;

                    // Find a new peak for this note.
                    for (int DistrIndex = 0; DistrIndex < DistributionsFound; DistrIndex++)
                    {
                        if (!NoteDistributions[DistrIndex].HasNote && // Hasn't already been turned into a note.
                            NoteDistributions[DistrIndex].Amplitude > MinDistributionValueNewNote) // The distribution is large enough to be worth turning into a new note.
                        {
                            // Create a new note with information from this distribution.
                            EnduringNoteID[NoteIndex] = CurrentNoteID++;
                            NoteDistributions[DistrIndex].HasNote = true; // Don't let this create/affect other notes this cycle.
                            Notes[NoteIndex].Amplitude = NoteDistributions[DistrIndex].Amplitude;
                            Notes[NoteIndex].Position = NoteDistributions[DistrIndex].Mean;
                            NotesAssociated[NoteIndex] = true; // This note was just created, so it is active this cycle.
                        }
                    }
                }
            }

            // Decay inactive notes.
            for (int NoteIndex = 0; NoteIndex < NotePeakMaxCount; NoteIndex++)
            {
                // Any notes that do not have a corresponding distribution this cycle should be decayed.
                if (!NotesAssociated[NoteIndex]) { Notes[NoteIndex].Amplitude *= (1F - NoteAttachAmplitudeIIRMultiplier); }

                float NewFiltered = Notes[NoteIndex].AmplitudeFiltered;
                NewFiltered *= (1F - NoteAttachAmplitudeIIRMultiplier2);
                NewFiltered += Notes[NoteIndex].Amplitude * NoteAttachAmplitudeIIRMultiplier2;
                Notes[NoteIndex].AmplitudeFiltered = NewFiltered; // A combination of the previous filtered value, pushed towards the current value.

                if (Notes[NoteIndex].AmplitudeFiltered < MinNoteAmplitude) // The amplitude is very small, just cut it to avoid noise.
                {
                    Notes[NoteIndex].Amplitude = 0;
                    Notes[NoteIndex].AmplitudeFiltered = 0;
                }
            }

            // Nudge down amplitude of all notes, zeroing out ones that don't make the cut.
            for (int NoteIndex = 0; NoteIndex < NotePeakMaxCount; NoteIndex++)
            {
                Notes[NoteIndex].AmplitudeFinal = Notes[NoteIndex].Amplitude - NoteOutputChop; // Reduce all notes by a small amount.
                if (Notes[NoteIndex].AmplitudeFinal < 0) { Notes[NoteIndex].AmplitudeFinal = 0; } // If this note is too small, zero it.
            }
        }

        /// <summary> Gets the distance between two elements that are on a circle that wraps between 0 and loopLength. </summary>
        /// <param name="a"> The location of point A </param>
        /// <param name="b"> The location of point B </param>
        /// <param name="loopLength"> The circumference of the circle. </param>
        /// <returns> The distance between the two points, always positive and max (loopLength / 2).</returns>
        private static float LoopDistance(float a, float b, float loopLength)
        {
            float Distance = Math.Abs(a - b); // The distance to go directly.
            Distance %= loopLength;
            if (Distance > loopLength / 2F) { Distance = loopLength - Distance; } // Distance around is shorter, so wrap around.
            return Distance; // Distance direct is shorter, just go directly.
        }

        /// <summary> Find the center point between two points, all on a circle, but with weights for each of the inputs. </summary>
        /// <param name="positionA"> The location of point A </param>
        /// <param name="weightA"> The size/weight of point A </param>
        /// <param name="positionB"> The location of point B </param>
        /// <param name="weightB"> The size/weight of point B </param>
        /// <param name="loopLength"> The circumference of the circle. </param>
        /// <returns> The location of the weighted center point. </returns>
        private static float LoopAverageWeighted(float positionA, float weightA, float positionB, float weightB, float loopLength)
        {
            float WeightSum = weightA + weightB;

            if (Math.Abs(positionA - positionB) > loopLength / 2F) // Looping around outside is shorter
            {
                if (positionA < positionB) { positionA += loopLength; } // Move A so that the direct distance B->A is positive and the shorter version.
                else { positionB += loopLength; } // Move B so that the direct distance A->B is positive and the shorter version.
            }
            // Because the distance has been corrected by moving a point if needed, this is now a standard weighted average.
            float Midpoint = ((positionA * weightA) + (positionB * weightB)) / WeightSum;
            return Midpoint % loopLength; // Move the point back onto the circle if it is outside.
        }

        /// <summary> A note, represented as a location, amplitude, and sigma, defining a normal (Gaussian) distribution. </summary>
        private struct NoteDistribution : IComparable<NoteDistribution>
        {
            /// <summary> The amplitude (relative strength) of the note. </summary>
            public float Amplitude;

            /// <summary> The mean (location) of the note in the frequency spectrum. </summary>
            /// <remarks> Range: 0.0 to <see cref="OctaveBinCount"/>. Fractional part shows where in the bin the peak is. </remarks>
            public float Mean;

            /// <summary> The sigma (spread) of the note. </summary>
            public float Sigma;

            /// <summary> Whether this distribution is affecting a note already. </summary>
            public bool HasNote;

            /// <summary> Compares the note amplitudes. </summary>
            /// <param name="other"> The note distribution to compare to. </param>
            /// <returns> </returns>
            public int CompareTo(NoteDistribution other)
            {
                if (this.Amplitude > other.Amplitude) { return -1; }
                else if (this.Amplitude < other.Amplitude) { return 1; }
                else { return 0; }
            }
        }

        public struct Note
        {
            /// <summary> Where on the scale this note is. </summary>
            public float Position; // TODO: What is this relative to?

            /// <summary> The note amplitude as of the previous cycle, with minimal filtering. </summary>
            public float Amplitude;

            /// <summary> The note amplitude, with some inter-frame smoothing applied. </summary>
            public float AmplitudeFiltered;

            /// <summary> The note amplitude, zeroed if very low amplitude. </summary>
            public float AmplitudeFinal;
        }

        [DllImport("ColorChordLib.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DoDFTProgressive32([In, Out] float[] OutBins, [In, Out] float[] Frequencies, int Bins, float[] DataBuffer, int DataBufferLoc, int DataBufferSize, float Q, float Speedup);

    }
}
