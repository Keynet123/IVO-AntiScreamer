// MainWindow.xaml.cs
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IVOAntiScreamer
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private WasapiLoopbackCapture capture;

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtResumeProcess(IntPtr processHandle);

        private const double AspectRatio = 16.0 / 10.0;

        // ПУЗЫРеке

        private readonly Random random = new();
        private readonly List<Bubble> bubbles = new();
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();

        private const int MaxBubbles = 20;
        private const float MinSize = 70;
        private const float MaxSize = 130;

        private SKBitmap? bitmap;
        private int surfaceWidth;
        private int surfaceHeight;

        private long lastFrameTime;
        private long fpsLastTime;

        private int blurCounter = 0;
        private const int BlurEveryNFrames = 0;

        public MainWindow()
        {
            InitializeComponent();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.Tick += Timer_Tick;
            timer.Start();
            StartAudioCapture();

            // Таймер для анимации пузырьков
            var renderTimer = new DispatcherTimer();
            renderTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
            renderTimer.Tick += OnRenderFrame;
            renderTimer.Start();

            Loaded += Window_Loaded;
        }

        public void onMenuButtonChange(int id)
        {
            if (id == 1)
            {
                MainGrid.Visibility = Visibility.Visible;
                SafeGrid.Visibility = Visibility.Hidden;
                SafeButton.IsEnabled = true;
                MenuButton.IsEnabled = false;
            }
            else if (id == 2) {
                MainGrid.Visibility = Visibility.Hidden;
                SafeGrid.Visibility = Visibility.Visible;
                SafeButton.IsEnabled = false;
                MenuButton.IsEnabled = true;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Установим стартовый размер 16:10 относительно текущего экрана
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            double targetWidth = screenWidth * 0.6;  // 60% от экрана
            double targetHeight = targetWidth / AspectRatio;

            if (targetHeight > screenHeight * 0.8)
            {
                targetHeight = screenHeight * 0.8;
                targetWidth = targetHeight * AspectRatio;
            }

            Width = targetWidth;
            Height = targetHeight;

            if (LavaSurface.ActualWidth <= 0 || LavaSurface.ActualHeight <= 0)
            {
                Dispatcher.BeginInvoke(() => Window_Loaded(sender, e), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            surfaceWidth = (int)LavaSurface.ActualWidth;
            surfaceHeight = (int)LavaSurface.ActualHeight;

            bubbles.Clear();
            for (int i = 0; i < MaxBubbles; i++)
                CreateBubble();

            bitmap = new SKBitmap(surfaceWidth, surfaceHeight);
            lastFrameTime = stopwatch.ElapsedMilliseconds;
            fpsLastTime = lastFrameTime;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Поддерживаем пропорции 16:10
            double currentWidth = e.NewSize.Width;
            double expectedHeight = currentWidth / AspectRatio;

            if (Math.Abs(expectedHeight - e.NewSize.Height) > 1)
            {
                Height = expectedHeight;
            }
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

        public void ResumeProcess(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                NtResumeProcess(process.Handle);
                Console.WriteLine($"▶️ Разморожен процесс: {process.ProcessName} (PID: {pid})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка разморозки: {ex.Message}");
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            float volume = AudioInterop.GetSystemMasterVolume();
            VolumeLabel.Text = $"{volume * 100:0.0}%";
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

                    DbValue.Text = $"{combinedVolume:0.0}%";
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
                                    ShowMessage(pid);
                                }

                            }
                        }
                    }


                });
            };


            capture.StartRecording();
            Status.Text = "Службы запущены";
        }

        private void ShowMessage(int pid)
        {
            string messageBoxText = "Заморожено!";
            var process = Process.GetProcessById(pid);
            string caption = $"Заморожено приложение {process.ProcessName} ({pid})! Разморозить?";
            MessageBoxButton button = MessageBoxButton.YesNoCancel;
            MessageBoxImage icon = MessageBoxImage.Warning;
            MessageBoxResult result;

            result = MessageBox.Show(caption, messageBoxText, button, icon);
            if (result == MessageBoxResult.Yes) {
                ResumeProcess(pid);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            capture?.StopRecording();
            capture?.Dispose();
        }

        private void CreateBubble()
        {
            float radius = (float)(MinSize + random.NextDouble() * (MaxSize - MinSize));
            float x = (float)(random.NextDouble() * surfaceWidth);
            float y = (float)(random.NextDouble() * surfaceHeight);
            float dx = (float)(random.NextDouble() * 100 - 50);
            float dy = (float)(random.NextDouble() * 100 - 50);

            var bubble = new Bubble(x, y, dx, dy, radius);
            bubble.SpeedMultiplier = 0.5f + (float)random.NextDouble() * 1.5f; // 0.5..2.0
            bubbles.Add(bubble);
        }


        private void OnRenderFrame(object? sender, EventArgs e)
        {
            long now = stopwatch.ElapsedMilliseconds;
            float dt = (now - lastFrameTime) / 1000f;
            if (dt > 0.05f) dt = 0.05f; // ограничение dt
            lastFrameTime = now;

            UpdatePhysics(dt);
            LavaSurface.InvalidateVisual();
        }

        private void LavaSurface_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            float centerX = surfaceWidth / 2f;
            float centerY = surfaceHeight / 2f;
            float radius = Math.Min(surfaceWidth, surfaceHeight) / 2f;

            // Рисуем фон
            // using var bgPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(10, 10, 15) };
            // canvas.DrawCircle(centerX, centerY, radius, bgPaint);

            // Ограничение клипом
            using var clipPath = new SKPath();
            clipPath.AddCircle(centerX, centerY, radius - 2);
            canvas.Save();
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            // Рисуем пузырьки
            DrawWithSoftwareRendering(canvas);

            canvas.Restore();

            // Рамка
            using var strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Color = new SKColor(30, 30, 30) };
            canvas.DrawCircle(centerX, centerY, radius - 2, strokePaint);
        }


        private void DrawDebugInfo(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 14,
                IsAntialias = true
            };

            long now = stopwatch.ElapsedMilliseconds;
            float fps = 1000f / Math.Max(now - fpsLastTime, 1);
            fpsLastTime = now;

            string info = $"CPU Metaballs | FPS: {fps:0} | Bubbles: {bubbles.Count}";
            canvas.DrawText(info, 10, 20, paint);
        }

        private void DrawWithSoftwareRendering(SKCanvas canvas)
        {
            if (bitmap == null) return;

            DrawMetaballsOptimized(canvas, bitmap);
        }

        private void DrawMetaballsOptimized(SKCanvas canvas, SKBitmap target)
        {
            int width = target.Width;
            int height = target.Height;
            const float Threshold = 7.0f;

            // Очистка bitmap
            using (var tempCanvas = new SkiaSharp.SKCanvas(target))
                tempCanvas.Clear(SkiaSharp.SKColors.Transparent);

            unsafe
            {
                IntPtr ptr = target.GetPixels();
                uint* pixelsPtr = (uint*)ptr.ToPointer();

                // Параллельная обработка по строкам
                Parallel.For(0, height, y =>
                {
                    int offset = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        float sum = 0f;
                        foreach (var b in bubbles)
                        {
                            // Ограничение области влияния пузыря
                            if (x < b.X - b.Radius * 3 || x > b.X + b.Radius * 3 ||
                                y < b.Y - b.Radius * 3 || y > b.Y + b.Radius * 3)
                                continue;

                            float dx = x - b.X;
                            float dy = y - b.Y;
                            sum += (b.Radius * b.Radius) / (dx * dx + dy * dy + 0.0001f);
                        }

                        if (sum >= Threshold)
                            pixelsPtr[offset + x] = 0xFF00C80A; // ARGB зеленый
                        else
                            pixelsPtr[offset + x] = 0x00000000; // прозрачный
                    }
                });
            }

            // Размытие каждые N кадров
            if (++blurCounter >= BlurEveryNFrames)
            {
                blurCounter = 0;
                using var blurPaint = new SkiaSharp.SKPaint
                {
                    ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(1, 1)
                };
                canvas.DrawBitmap(target, 0, 0, blurPaint);
            }
            else
            {
                canvas.DrawBitmap(target, 0, 0);
            }
        }

        private void UpdatePhysics(float dt)
        {
            float centerX = surfaceWidth / 2f;
            float centerY = surfaceHeight / 2f;
            float radius = Math.Min(surfaceWidth, surfaceHeight) / 2f - 2;
            float horizontalJitter = 10f;
            float baseSpeed = 15f; // базовая вертикальная скорость
            float maxHorizontal = 30f;
            float spawnBuffer = 5f;

            foreach (var b in bubbles)
            {
                float verticalVariation = (float)(random.NextDouble() * 10f - 5f); // ±5
                b.DY = -(baseSpeed + verticalVariation) * b.SpeedMultiplier;  // применяем множитель

                b.DX += (float)(random.NextDouble() - 0.5) * horizontalJitter * dt;
                b.DX = Math.Clamp(b.DX, -maxHorizontal, maxHorizontal);

                b.X += b.DX * dt;
                b.Y += b.DY * dt;

                // --- Верхняя граница ---
                if (b.Y + b.Radius < centerY - radius)
                {
                    // Случайное X внутри круга
                    float spawnY = centerY + radius - b.Radius - spawnBuffer;
                    float maxXOffset = MathF.Sqrt(MathF.Max(radius * radius - (spawnY - centerY) * (spawnY - centerY), 0));
                    maxXOffset = Math.Max(maxXOffset, 5f);
                    b.X = centerX + ((float)random.NextDouble() - 0.5f) * maxXOffset * 2;
                    b.Y = spawnY;
                }

                // --- Нижняя граница ---
                else if (b.Y - b.Radius > centerY + radius)
                {
                    float spawnY = centerY - radius + b.Radius + spawnBuffer;
                    float maxXOffset = MathF.Sqrt(MathF.Max(radius * radius - (spawnY - centerY) * (spawnY - centerY), 0));
                    maxXOffset = Math.Max(maxXOffset, 5f);
                    b.X = centerX + ((float)random.NextDouble() - 0.5f) * maxXOffset * 2;
                    b.Y = spawnY;
                }

                // --- Демпфирование колебаний для плавности ---
                b.DX *= 0.97f;
            }

            // --- Опционально: добавление новых пузырьков, если нужно ---
            int maxBubbles = 10; // например
            while (bubbles.Count < maxBubbles)
            {
                float angle = (float)(random.NextDouble() * Math.PI); // 0..π
                float r = radius * (float)Math.Sqrt(random.NextDouble());
                float x = centerX + r * (float)Math.Cos(angle);
                float y = centerY + radius - (float)Math.Sqrt(radius * radius - r * r); // появление внизу
                float dx = (float)(random.NextDouble() - 0.5) * 10f;
                float dy = -(baseSpeed + (float)(random.NextDouble() * 10.0 - 5.0));
                float bubbleRadius = 5f + (float)random.NextDouble() * 10f;

                bubbles.Add(new Bubble(x, y, dx, dy, bubbleRadius));
            }
        }

        private void SafeButton_Click(object sender, RoutedEventArgs e)
        {
            onMenuButtonChange(2);
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            onMenuButtonChange(1);
        }
    }

    public class Bubble
    {
        public float X, Y;
        public float DX, DY;
        public float Radius;
        public float SpeedMultiplier; // новый коэффициент скорости

        public Bubble(float x, float y, float dx, float dy, float radius)
        {
            X = x;
            Y = y;
            DX = dx;
            DY = dy;
            Radius = radius;
            SpeedMultiplier = 0.5f + (float)new Random().NextDouble() * 1.5f; // 0.5..2.0
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
