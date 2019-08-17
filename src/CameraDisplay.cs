using DotImaging;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Scopie
{
    class CameraDisplay
    {
        class DoubleBufferedForm : Form
        {
            public DoubleBufferedForm()
            {
                DoubleBuffered = true;
            }
        }

        private readonly QhyCcd _camera;
        private readonly Form _form;
        private readonly Bitmap _bitmap;
        private string _status;
        private int _save;
        private bool _solve;
        private Task? _solveTask;

        public CameraDisplay(QhyCcd camera)
        {
            _camera = camera;
            _form = new DoubleBufferedForm();
            _status = "starting...";
            Console.WriteLine(camera.Width);
            Console.WriteLine(camera.Height);
            _bitmap = new Bitmap(camera.Width, camera.Height, PixelFormat.Format32bppArgb);
            _form.Paint += OnPaint;
        }

        public void Start()
        {
            var formThread = new Thread(() => Application.Run(_form));
            formThread.SetApartmentState(ApartmentState.STA);
            formThread.IsBackground = true;
            formThread.Start();

            var imageThread = new Thread(() => DoImage())
            {
                IsBackground = true
            };
            imageThread.Start();
        }

        public void Save(int n) => _save += n;

        internal void Solve() => _solve = true;

        private void OnPaint(object sender, PaintEventArgs e)
        {
            var calcWidth = _form.Height * _bitmap.Width / _bitmap.Height;
            var calcHeight = _form.Width * _bitmap.Height / _bitmap.Width;
            var realWidth = Math.Min(_form.Width, calcWidth);
            var realHeight = Math.Min(_form.Height, calcHeight);
            e.Graphics.DrawImage(_bitmap, new Rectangle(0, 0, realWidth, realHeight));
            e.Graphics.DrawString(_status, SystemFonts.DefaultFont, Brushes.Red, 1, 1);
        }

        private double Mean(ushort[] data)
        {
            var sum = 0.0;
            foreach (var datum in data)
            {
                sum += datum;
            }
            return sum / data.Length;
        }

        private double Stdev(ushort[] data, double mean)
        {
            var sum = 0.0;
            foreach (var datum in data)
            {
                var diff = (datum - mean);
                sum += diff * diff;
            }
            return Math.Sqrt(sum / data.Length);
        }

        private int[] ProcessImage(ushort[] data)
        {
            var start = Stopwatch.StartNew();
            var mean = Mean(data);
            var stdev = Stdev(data, mean);
            var pixels = new int[data.Length];

            // ((x - mean) / (stdev * size) + 0.5) * 255
            // ((x / (stdev * size) - mean / (stdev * size)) + 0.5) * 255
            // (x / (stdev * size) - mean / (stdev * size) + 0.5) * 255
            // x / (stdev * size) * 255 - mean / (stdev * size) * 255 + 0.5 * 255
            // x * (255 / (stdev * size)) + (-mean / (stdev * size) + 0.5) * 255
            // x * a + b
            const double SIZE = 3.0;
            var a = 255 / (stdev * SIZE);
            var b = 255 * (-mean / (stdev * SIZE) + 0.5);
            for (var i = 0; i < pixels.Length; i++)
            {
                var value = (byte)Math.Max(0.0, Math.Min(255.0, data[i] * a + b));
                pixels[i] = (value << 16) | (value << 8) | value;
            }

            var procMs = start.ElapsedMilliseconds;
            _status = $"Time to proc: {procMs}\nmean: {mean:f3}\nstdev: {stdev:f3}";
            return pixels;
        }

        private void ProcessSolve(ushort[] rawShortPixels)
        {
            if (_solveTask != null && (_solveTask.IsCompleted || _solveTask.IsFaulted || _solveTask.IsCanceled))
            {
                _solveTask.Wait();
                _solveTask = null;
            }
            if (_solve && _solveTask == null)
            {
                _solve = false;
                _solveTask = DoSolve(rawShortPixels, _camera.Width, _camera.Height);
            }
        }

        private async Task DoSolve(ushort[] pixels, int width, int height)
        {
            var result = await PlateSolve.Solve(pixels, width, height);
            if (result.HasValue)
            {
                var (ra, dec) = result.Value;
                Console.WriteLine("Solved position (degrees):");
                Console.WriteLine($"{ra.Degrees}d {dec.Degrees}d");
                Console.WriteLine("Solved position (dms):");
                Console.WriteLine($"{ra.ToDmsString(Dms.Unit.Degrees)} {dec.ToDmsString(Dms.Unit.Degrees)}");
                Console.WriteLine("Solved position (hms/dms):");
                Console.WriteLine($"{ra.ToDmsString(Dms.Unit.Hours)} {dec.ToDmsString(Dms.Unit.Degrees)}");
            }
            else
            {
                Console.WriteLine("Failed to solve");
            }
        }

        private void DoImage()
        {
            _camera.StartExposure();
            byte[]? rawBytePixels = null;
            while (true)
            {
                while (!_camera.GetExposure(ref rawBytePixels) || rawBytePixels == null) { }
                var rawShortPixels = new ushort[rawBytePixels.Length / 2];
                Buffer.BlockCopy(rawBytePixels, 0, rawShortPixels, 0, rawBytePixels.Length);
                if (_save > 0)
                {
                    _save--;
                    SaveImage(rawShortPixels, _camera.Width, _camera.Height);
                }
                ProcessSolve(rawShortPixels);
                var pixels = ProcessImage(rawShortPixels);
                try
                {
                    _form.BeginInvoke((Action)(() =>
                    {
                        var locked = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        Marshal.Copy(pixels, 0, locked.Scan0, locked.Width * locked.Height);
                        _bitmap.UnlockBits(locked);
                        _form.Invalidate();
                    }));
                }
                catch
                {
                    // form is closed
                    break;
                }
            }
            _camera.StopExposure();
        }

        private static void SaveImage(ushort[] pixels, int width, int height)
        {
            var greyPixels = new Gray<ushort>[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    greyPixels[y, x] = pixels[y * width + x];
                }
            }
            var filename = GetNextImageFilename();
            greyPixels.Save(filename);
            Console.WriteLine("Saved image");
        }

        private static string GetNextImageFilename()
        {
            var now = DateTime.Now;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{now.Year}-{now.Month}-{now.Day}");
            Directory.CreateDirectory(dir);
            var baseFilename = $"telescope.{now.Year}-{now.Month}-{now.Day}.{now.Hour}-{now.Minute}-{now.Second}";
            var filename = Path.Combine(dir, $"{baseFilename}.png");
            for (var index = 1; File.Exists(filename); index++)
            {
                filename = Path.Combine(dir, $"{baseFilename}_{index}.png");
            }
            // @jaredpar, this race condition is for you <3
            return filename;
        }
    }
}
