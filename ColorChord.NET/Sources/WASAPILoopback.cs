﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vannatech.CoreAudio.Constants;
using Vannatech.CoreAudio.Enumerations;
using Vannatech.CoreAudio.Externals;
using Vannatech.CoreAudio.Interfaces;

namespace ColorChord.NET.Sources
{
    public class WASAPILoopback : IAudioSource
    {
        private const CLSCTX CLSCTX_ALL = CLSCTX.CLSCTX_INPROC_SERVER | CLSCTX.CLSCTX_INPROC_HANDLER | CLSCTX.CLSCTX_LOCAL_SERVER | CLSCTX.CLSCTX_REMOTE_SERVER;
        private const ulong BufferLength = 50 * 10000; // 50 ms, in ticks
        private ulong ActualBufferDuration;
        private int BytesPerFrame;
        private bool UseInput = false;

        private bool KeepGoing = true;
        private bool StreamReady = false;
        private Thread ProcessThread;

        private IAudioClient Client;
        private IAudioCaptureClient CaptureClient;
        private AudioTools.WAVEFORMATEX MixFormat;

        public WASAPILoopback(string name) { }

        public void ApplyConfig(Dictionary<string, object> options)
        {
            Log.Info("Reading config for WASAPILoopback.");
            this.UseInput = ConfigTools.CheckBool(options, "useInput", false, true);
            ConfigTools.WarnAboutRemainder(options, typeof(IAudioSource));
        }

        public void Start() // TOOD: Make device, etc selection possible instead of using defaults.
        {
            int ErrorCode; // Used to track error codes for each operation.
            Type DeviceEnumeratorType = Type.GetTypeFromCLSID(new Guid(ComCLSIDs.MMDeviceEnumeratorCLSID));
            IMMDeviceEnumerator DeviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(DeviceEnumeratorType);

            Console.WriteLine("Audio output device list:");
            ListDevices(DeviceEnumerator, EDataFlow.eRender);

            Console.WriteLine("Audio input device list:");
            ListDevices(DeviceEnumerator, EDataFlow.eCapture);

            ErrorCode = DeviceEnumerator.GetDefaultAudioEndpoint(this.UseInput ? EDataFlow.eCapture : EDataFlow.eRender, this.UseInput ? ERole.eMultimedia : ERole.eConsole, out IMMDevice Device);
            Marshal.ThrowExceptionForHR(ErrorCode);

            ErrorCode = Device.Activate(new Guid(ComIIDs.IAudioClientIID), (uint)CLSCTX_ALL, IntPtr.Zero, out object ClientObj);
            Marshal.ThrowExceptionForHR(ErrorCode);
            this.Client = (IAudioClient)ClientObj;

            ErrorCode = this.Client.GetMixFormat(out IntPtr MixFormatPtr);
            this.MixFormat = AudioTools.FormatFromPointer(MixFormatPtr);
            Marshal.ThrowExceptionForHR(ErrorCode);

            Console.WriteLine("Audio format detected: ");
            Console.WriteLine("  Channels: " + this.MixFormat.nChannels);
            Console.WriteLine("  Sample rate: " + this.MixFormat.nSamplesPerSec);
            Console.WriteLine("  Bits per sample: " + this.MixFormat.wBitsPerSample);
            this.BytesPerFrame = this.MixFormat.nChannels * (this.MixFormat.wBitsPerSample / 8);

            NoteFinder.SetSampleRate((int)this.MixFormat.nSamplesPerSec);

            ErrorCode = this.Client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, this.UseInput ? AUDCLNT_STREAMFLAGS_XXX.AUDCLNT_STREAMFLAGS_NOPERSIST : AUDCLNT_STREAMFLAGS_XXX.AUDCLNT_STREAMFLAGS_LOOPBACK, BufferLength, 0, MixFormatPtr);
            Marshal.ThrowExceptionForHR(ErrorCode);

            ErrorCode = this.Client.GetBufferSize(out uint BufferFrameCount);
            Marshal.ThrowExceptionForHR(ErrorCode);

            ErrorCode = this.Client.GetService(new Guid(ComIIDs.IAudioCaptureClientIID), out object CaptureClientObj);
            Marshal.ThrowExceptionForHR(ErrorCode);
            this.CaptureClient = (IAudioCaptureClient)CaptureClientObj;

            this.ActualBufferDuration = (ulong)((double)BufferLength * BufferFrameCount / this.MixFormat.nSamplesPerSec);

            ErrorCode = this.Client.Start();
            Marshal.ThrowExceptionForHR(ErrorCode);
            this.StreamReady = true;

            this.KeepGoing = true;
            this.ProcessThread = new Thread(ProcessAudio);
            this.ProcessThread.Name = "WASAPILoopback";
            this.ProcessThread.Start();
        }

        private void ListDevices(IMMDeviceEnumerator enumerator, EDataFlow dataFlow)
        {
            int ErrorCode;
            ErrorCode = enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATE_XXX.DEVICE_STATE_ACTIVE, out IMMDeviceCollection Devices);
            Marshal.ThrowExceptionForHR(ErrorCode);

            ErrorCode = Devices.GetCount(out uint DeviceCount);
            Marshal.ThrowExceptionForHR(ErrorCode);

            for(uint DeviceIndex = 0; DeviceIndex < DeviceCount; DeviceIndex++) // TODO: Consider checking error codes.
            {
                ErrorCode = Devices.Item(DeviceIndex, out IMMDevice Device);
                ErrorCode = Device.GetId(out string DeviceID);
                ErrorCode = Device.OpenPropertyStore(STGM.STGM_READ, out IPropertyStore Properties);

                string DeviceFriendlyName = "[Name Retrieval Failed]";
                ErrorCode = Properties.GetCount(out uint PropertyCount);
                for (uint PropIndex = 0; PropIndex < PropertyCount; PropIndex++)
                {
                    ErrorCode = Properties.GetAt(PropIndex, out PROPERTYKEY Property);
                    if (Property.fmtid == PropertyKeys.PKEY_DeviceInterface_FriendlyName)
                    {
                        ErrorCode = Properties.GetValue(ref Property, out PROPVARIANT Variant);
                        DeviceFriendlyName = Marshal.PtrToStringUni(Variant.Data.AsStringPtr);
                        break;
                    }
                }

                if (Marshal.GetExceptionForHR(ErrorCode) == null) { Console.WriteLine("Device #" + DeviceIndex + " is \"" + DeviceFriendlyName + "\"."); }
                else { Console.WriteLine("Could not get info for device #" + DeviceIndex + ", got HRESULT " + ErrorCode); }
            }
        }

        public void Stop()
        {
            this.KeepGoing = false;
            this.ProcessThread.Join();
        }

        private void ProcessAudio()
        {
            int ErrorCode;

            while (this.KeepGoing)
            {
                Thread.Sleep((int)(this.ActualBufferDuration / (BufferLength / 1000) / 2));

                ErrorCode = this.CaptureClient.GetNextPacketSize(out uint PacketLength);
                Marshal.ThrowExceptionForHR(ErrorCode);

                while (PacketLength != 0)
                {
                    ErrorCode = this.CaptureClient.GetBuffer(out IntPtr DataArray, out uint NumFramesAvail, out AUDCLNT_BUFFERFLAGS BufferStatus, out ulong DevicePosition, out ulong CounterPosition);
                    Marshal.ThrowExceptionForHR(ErrorCode);

                    if (BufferStatus.HasFlag(AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT))
                    {
                        NoteFinder.AudioBuffer[NoteFinder.AudioBufferHead] = 0;
                        NoteFinder.AudioBufferHead = (NoteFinder.AudioBufferHead + 1) % NoteFinder.AudioBuffer.Length;
                    }
                    else
                    {
                        byte[] AudioData = new byte[NumFramesAvail * this.BytesPerFrame];
                        Marshal.Copy(DataArray, AudioData, 0, (int)(NumFramesAvail * this.BytesPerFrame));

                        for (int i = 0; i < NumFramesAvail; i++)
                        {
                            float Sample = 0;
                            // TODO: Make multi-channel downmixing toggleable, maybe some stereo visualizations?
                            for (int c = 0; c < this.MixFormat.nChannels; c++) { Sample += BitConverter.ToSingle(AudioData, (i * this.BytesPerFrame) + ((this.MixFormat.wBitsPerSample / 8) * c)); }
                            NoteFinder.AudioBuffer[NoteFinder.AudioBufferHead] = Sample / this.MixFormat.nChannels; // Use the average of the channels.
                            NoteFinder.AudioBufferHead = (NoteFinder.AudioBufferHead + 1) % NoteFinder.AudioBuffer.Length;
                        }
                        NoteFinder.LastDataAdd = DateTime.UtcNow;
                    }

                    ErrorCode = this.CaptureClient.ReleaseBuffer(NumFramesAvail);
                    Marshal.ThrowExceptionForHR(ErrorCode);

                    ErrorCode = this.CaptureClient.GetNextPacketSize(out PacketLength);
                    Marshal.ThrowExceptionForHR(ErrorCode);
                }
                //Console.WriteLine("Got audio data, head now at position " + NoteFinder.AudioBufferHead);
            }

            ErrorCode = this.Client.Stop();
            Marshal.ThrowExceptionForHR(ErrorCode);
            this.StreamReady = false;
        }

    }
}