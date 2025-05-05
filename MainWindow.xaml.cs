// MainWindow.xaml.cs
using NAudio.Wave;
using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VolumeMeter
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private WasapiLoopbackCapture capture;

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSuspendProcess(IntPtr processHandle);
        public MainWindow()
        {
            InitializeComponent();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += Timer_Tick;
            timer.Start();
            StartAudioCapture();
        }

        private void SuspendProcess(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                NtSuspendProcess(process.Handle);
                Console.WriteLine($"🔇 Заморожен процесс: {process.ProcessName} (PID: {pid})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка заморозки: {ex.Message}");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            float volume = AudioInterop.GetSystemMasterVolume();
            VolumeLabel.Content = $"Громкость: {volume * 100:0.0}%";
        }

        private void StartAudioCapture()
        {
            capture = new WasapiLoopbackCapture();
            capture.DataAvailable += (s, a) =>
            {
                int bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
                int sampleCount = a.BytesRecorded / bytesPerSample;
                float sum = 0;

                for (int i = 0; i < a.BytesRecorded; i += bytesPerSample)
                {
                    float sample = BitConverter.ToSingle(a.Buffer, i);
                    sum += sample * sample;
                }

                float rms = (float)Math.Sqrt(sum / sampleCount);
                float db = 20 * (float)Math.Log10(rms + 1e-10f); // добавим маленькое значение, чтобы избежать логарифма 0

                Dispatcher.Invoke(() =>
                {
                    float db = 20 * (float)Math.Log10(rms + 1e-10f); // типичный диапазон: -80..0
                    float normalized = Math.Clamp((db + 80f) / 80f, 0f, 1f); // теперь 0..1
                    float volumePercent = normalized * 100f;
                    float systemVolume = AudioInterop.GetSystemMasterVolume(); // от 0.0 до 1.0
                    float combinedVolume = volumePercent * systemVolume; // результат в процентах

                    Value.Content = $"Громкость в дб: {combinedVolume:0.0}%";
                    if (combinedVolume > 90)
                    {
                        var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                        var sessions = device.AudioSessionManager.Sessions;

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];

                            // Проверка: сессия активна и производит громкий звук
                            if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive &&
                                session.AudioMeterInformation.MasterPeakValue * 100 * systemVolume * 100 > 75)
                            {
                                int pid = (int)session.GetProcessID;
                                int currentPid = Process.GetCurrentProcess().Id;

                                if (pid != currentPid)
                                {
                                    SuspendProcess(pid);
                                }

                            }
                        }
                    }


                });
            };


            capture.StartRecording();
            Status.Content = "Запись начата";
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            capture?.StopRecording();
            capture?.Dispose();
        }
    }

    public static class AudioInterop
    {
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        private enum EDataFlow { eRender, eCapture, eAll }
        private enum ERole { eConsole, eMultimedia, eCommunications }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            [PreserveSig]
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out int pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            // остальные методы опущены
        }

        public static float GetSystemMasterVolume()
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia
            );
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
    }
}
