using System;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Scopie
{
    public class Mount
    {
        private readonly SerialPort _port;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        private readonly byte[] _readBuffer = new byte[1];

        public static Mount? Create()
        {
            var ports = SerialPort.GetPortNames();
            return ports.Length == 1 ? new Mount(ports[0]) : null;
        }

        public static string[] Ports() => SerialPort.GetPortNames();

        public Mount(string port)
        {
            _port = new SerialPort(port, 9600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
            };
            _port.Open();
        }

        private async Task<string> Interact(string command)
        {
            /*
            static void Debug(string prefix, string value)
            {
                var nums = string.Join(", ", value.Select(c => (int)c));
                if (value.Length > 0 && value.All(c => c >= 32 && c < 128))
                {
                    nums += $" ({value})";
                }
                Console.WriteLine($"{prefix} {nums}");
            }
            */

            Task Write(string cmd)
            {
                //Debug("  >", cmd);
                var array = new byte[cmd.Length];
                var i = 0;
                foreach (var chr in cmd)
                {
                    array[i++] = (byte)chr;
                }
                return _port.BaseStream.WriteAsync(array, 0, array.Length);
            }

            async Task<string> ReadLine()
            {
                var builder = new StringBuilder();
                while (true)
                {
                    var length = await _port.BaseStream.ReadAsync(_readBuffer, 0, 1).ConfigureAwait(false);
                    var data = (char)_readBuffer[0];
                    // all responses end with #
                    if (length <= 0 || data == '#')
                    {
                        break;
                    }
                    builder.Append(data);
                }
                var result = builder.ToString();
                // Debug("  <", result);
                return result;
            }

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await Write(command).ConfigureAwait(false);
                return await ReadLine().ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<(Dms, Dms)> GetRaDec()
        {
            var line = await Interact("e").ConfigureAwait(false);
            var split = line.Split(',');
            if (split.Length != 2)
            {
                throw new Exception($"Invalid response to 'e': {line}");
            }
            var ra = Convert.ToUInt32(split[0], 16) / (uint.MaxValue + 1.0);
            var dec = Convert.ToUInt32(split[1], 16) / (uint.MaxValue + 1.0);
            return (Dms.From0to1(ra), Dms.From0to1(dec));
        }

        private static string ToMountHex(Dms value)
        {
            var intval = (uint)(value.ValueMod * (uint.MaxValue + 1.0));
            intval &= 0xffffff00;
            return intval.ToString("X8");
        }

        public async Task OverwriteRaDec(Dms ra, Dms dec)
        {
            var res = await Interact($"s{ToMountHex(ra)},{ToMountHex(dec)}").ConfigureAwait(false);
            if (res != "")
            {
                throw new Exception($"Overwrite RA/DEC failed: {res}");
            }
        }

        public async Task Slew(Dms ra, Dms dec)
        {
            var res = await Interact($"r{ToMountHex(ra)},{ToMountHex(dec)}").ConfigureAwait(false);
            if (res != "")
            {
                throw new Exception($"Slew RA/DEC failed: {res}");
            }
        }

        public async Task<(Dms, Dms)> GetAzAlt()
        {
            var line = await Interact("z").ConfigureAwait(false);
            var split = line.Split(',');
            if (split.Length != 2)
            {
                throw new Exception($"Invalid response to 'z': {line}");
            }
            var az = Convert.ToUInt32(split[0], 16) / (uint.MaxValue + 1.0);
            var alt = Convert.ToUInt32(split[1], 16) / (uint.MaxValue + 1.0);
            return (Dms.From0to1(az), Dms.From0to1(alt));
        }

        public async Task SlewAzAlt(Dms az, Dms alt)
        {
            var res = await Interact($"b{ToMountHex(az)},{ToMountHex(alt)}").ConfigureAwait(false);
            if (res != "")
            {
                throw new Exception($"Slew az/alt failed: {res}");
            }
        }

        public async Task CancelSlew()
        {
            var res = await Interact("M").ConfigureAwait(false);
            if (res != "")
            {
                throw new Exception($"Cancel slew failed: {res}");
            }
        }

        public enum TrackingMode
        {
            Off = 0,
            AltAz = 1,
            Equatorial = 2,
            SiderealPec = 3,
        }

        public async Task<TrackingMode> GetTrackingMode()
        {
            var result = await Interact("t").ConfigureAwait(false);
            return (TrackingMode)(int)result[0];
        }

        public async Task SetTrackingMode(TrackingMode mode)
        {
            var modeStr = (char)(int)mode;
            var result = await Interact($"T{modeStr}").ConfigureAwait(false);
            if (result != "")
            {
                throw new Exception($"Set tracking mode failed: {result}");
            }
        }

        private string FormatLatLon(Dms lat, Dms lon)
        {
            var (latSign, latDeg, latMin, latSec, _) = lat.DegreesMinutesSeconds;
            var (lonSign, lonDeg, lonMin, lonSec, _) = lon.DegreesMinutesSeconds;
            // The format of the location commands is: ABCDEFGH, where:
            // A is the number of degrees of latitude.
            // B is the number of minutes of latitude.
            // C is the number of seconds of latitude.
            // D is 0 for north and 1 for south.
            // E is the number of degrees of longitude.
            // F is the number of minutes of longitude.
            // G is the number of seconds of longitude.
            // H is 0 for east and 1 for west.
            var builder = new StringBuilder(8);
            builder.Append((char)latDeg);
            builder.Append((char)latMin);
            builder.Append((char)latSec);
            builder.Append(latSign ? (char)1 : (char)0);
            builder.Append((char)lonDeg);
            builder.Append((char)lonMin);
            builder.Append((char)lonSec);
            builder.Append(lonSign ? (char)1 : (char)0);
            return builder.ToString();
        }

        private (Dms, Dms) ParseLatLon(string value)
        {
            if (value.Length != 8)
            {
                throw new Exception($"Invalid lat/lon: {value}");
            }
            var latDeg = (int)value[0];
            var latMin = (int)value[1];
            var latSec = (int)value[2];
            var latSign = value[3] == 1;
            var lonDeg = (int)value[4];
            var lonMin = (int)value[5];
            var lonSec = (int)value[6];
            var lonSign = value[7] == 1;
            var lat = Dms.FromDms(latSign, latDeg, latMin, latSec);
            var lon = Dms.FromDms(lonSign, lonDeg, lonMin, lonSec);
            return (lat, lon);
        }

        public async Task<(Dms lat, Dms lon)> GetLocation()
        {
            var result = await Interact("w").ConfigureAwait(false);
            return ParseLatLon(result);
        }

        public async Task SetLocation(Dms lat, Dms lon)
        {
            var location = FormatLatLon(lat, lon);
            var result = await Interact($"W{location}").ConfigureAwait(false);
            if (result != "")
            {
                throw new Exception($"Set location failed: {result}");
            }
        }

        private DateTime ParseTime(string time)
        {
            if (time.Length != 8)
            {
                throw new Exception($"Invalid time: {time}");
            }
            var hour = (int)time[0];
            var minute = (int)time[1];
            var second = (int)time[2];
            var month = (int)time[3];
            var day = (int)time[4];
            var year = (int)time[5] + 2000;
            var timeZoneOffset = (int)time[6];
            var dst = time[7] == 1;

            if (dst)
            {
                timeZoneOffset -= 1;
            }
            if (timeZoneOffset >= 128)
            {
                timeZoneOffset -= 256;
            }

            var res = new DateTime(year, month, day, hour, minute, second, 0, DateTimeKind.Local);

            var currentTimeZoneOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).Hours;
            if (currentTimeZoneOffset != timeZoneOffset)
            {
                var delta = timeZoneOffset - currentTimeZoneOffset;
                Console.WriteLine("Mount thinks it's in a different timezone?");
                Console.WriteLine($"Mount thinks: {timeZoneOffset}");
                Console.WriteLine($"Computer thinks: {currentTimeZoneOffset}");
                Console.WriteLine($"Adding {delta} to reported hour");
                res.AddHours(delta);
            }
            return res;
        }

        private string FormatTime(DateTime time)
        {
            var timeZoneOffset = TimeZoneInfo.Local.GetUtcOffset(time).Hours;
            if (timeZoneOffset < 0)
            {
                timeZoneOffset += 256;
            }
            // Q is the hour (24 hour clock).
            // R is the minutes.
            // S is the seconds.
            // T is the month.
            // U is the day.
            // V is the year (century assumed as 20).
            // W is the offset from GMT for the time zone. Note: if zone is negative, use 256 - zone.
            // X is 1 to enable Daylight Savings and 0 for Standard Time
            var builder = new StringBuilder();
            builder.Append((char)time.Hour);
            builder.Append((char)time.Minute);
            builder.Append((char)time.Second);
            builder.Append((char)time.Month);
            builder.Append((char)time.Day);
            builder.Append((char)(time.Year - 2000));
            builder.Append((char)timeZoneOffset);
            builder.Append((char)0); // DST is already calculated in .net
            return builder.ToString();
        }

        public async Task<DateTime> GetTime()
        {
            var result = await Interact("h").ConfigureAwait(false);
            return ParseTime(result);
        }

        public async Task SetTime(DateTime time)
        {
            var location = FormatTime(time);
            var result = await Interact($"H{location}").ConfigureAwait(false);
            if (result != "")
            {
                throw new Exception($"Set time failed: {result}");
            }
        }

        public async Task<bool> IsAligned()
        {
            var aligned = await Interact("J").ConfigureAwait(false);
            return aligned != "0";
        }

        public async Task<char> Echo(char c)
        {
            var echo = await Interact($"K{c}").ConfigureAwait(false);
            return echo[0];
        }

        static void SplitThreeBytes(Dms valueDms, out byte high, out byte med, out byte low)
        {
            var value = valueDms.ValueMod;
            value *= 256;
            high = (byte)value;
            value = Dms.Mod(value, 1);
            value *= 256;
            med = (byte)value;
            value = Dms.Mod(value, 1);
            value *= 256;
            low = (byte)value;
        }

        async Task PCommandThree(byte one, byte two, byte three, Dms data)
        {
            SplitThreeBytes(data, out var high, out var med, out var low);
            var cmd = new char[8];
            cmd[0] = 'P';
            cmd[1] = (char)one;
            cmd[2] = (char)two;
            cmd[3] = (char)three;
            cmd[4] = (char)high;
            cmd[5] = (char)med;
            cmd[6] = (char)low;
            cmd[7] = (char)0;
            await Interact(new string(cmd)).ConfigureAwait(false);
        }

        public Task ResetRA(Dms data) => PCommandThree(4, 16, 4, data);

        public Task ResetDec(Dms data) => PCommandThree(4, 17, 4, data);

        public Task SlowGotoRA(Dms data) => PCommandThree(4, 16, 23, data);

        public Task SlowGotoDec(Dms data) => PCommandThree(4, 17, 23, data);

        async Task FixedSlewCommand(byte one, byte two, byte three, byte rate)
        {
            var cmd = new char[8];
            cmd[0] = 'P';
            cmd[1] = (char)one;
            cmd[2] = (char)two;
            cmd[3] = (char)three;
            cmd[4] = (char)rate;
            cmd[5] = (char)0;
            cmd[6] = (char)0;
            cmd[7] = (char)0;
            await Interact(new string(cmd)).ConfigureAwait(false);
        }

        public Task FixedSlewRA(int speed) => speed > 0 ?
            FixedSlewCommand(2, 16, 36, (byte)speed) :
            FixedSlewCommand(2, 16, 37, (byte)-speed);

        public Task FixedSlewDec(int speed) => speed > 0 ?
            FixedSlewCommand(2, 17, 36, (byte)speed) :
            FixedSlewCommand(2, 17, 37, (byte)-speed);
    }
}
