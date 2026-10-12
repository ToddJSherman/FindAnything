using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FindAnything.Core
{
    public sealed class DiskInfo
    {
        public int Index;
        public string Model = "";
        public string Serial = "";
        public string Interface = "";
        public long Size;
        public string Letters = "";        // "C: D:" for volumes on this disk
        public bool System;                // holds the Windows drive

        public string Path { get { return @"\\.\PhysicalDrive" + Index; } }

        public override string ToString()
        {
            return "Disk " + Index + "  " + Model.Trim() + "  (" + SizeText(Size) + (Letters.Length > 0 ? ", " + Letters : ", no drive letters") + ")";
        }

        public static string SizeText(long b)
        {
            double v = b;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }   // drive makers count in 1000s
            return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
        }
    }

    /// <summary>Raw, read-only access to whole physical drives (needs administrator rights).</summary>
    public static class RawDisk
    {
        public static bool IsAdmin
        {
            get
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static List<DiskInfo> List()
        {
            List<DiskInfo> disks = new List<DiskInfo>();
            string winDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory).Substring(0, 2).ToUpperInvariant();
            using (ManagementObjectSearcher s = new ManagementObjectSearcher("SELECT Index, Model, SerialNumber, InterfaceType, Size, DeviceID FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject d in s.Get())
                {
                    DiskInfo info = new DiskInfo();
                    info.Index = Convert.ToInt32(d["Index"]);
                    info.Model = (d["Model"] as string) ?? "";
                    info.Serial = ((d["SerialNumber"] as string) ?? "").Trim();
                    info.Interface = (d["InterfaceType"] as string) ?? "";
                    info.Size = d["Size"] == null ? 0 : Convert.ToInt64(d["Size"]);
                    List<string> letters = LettersOn(info.Index);
                    letters.Sort();
                    info.Letters = string.Join(" ", letters.ToArray());
                    info.System = letters.Contains(winDrive);
                    disks.Add(info);
                }
            }
            disks.Sort(delegate(DiskInfo a, DiskInfo b) { return a.Index.CompareTo(b.Index); });
            return disks;
        }

        /// <summary>Drive letters whose volume lives on the given physical disk (asks each volume; no admin needed).</summary>
        private static List<string> LettersOn(int disk)
        {
            List<string> letters = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                string root = d.Name.Substring(0, 2);
                using (SafeFileHandle h = CreateFile(@"\\.\" + root, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    byte[] buf = new byte[8 + 24 * 8];
                    int returned;
                    if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, buf.Length, out returned, IntPtr.Zero)) continue;
                    int count = BitConverter.ToInt32(buf, 0);
                    for (int i = 0; i < count && 8 + 24 * i + 4 <= returned; i++)
                        if (BitConverter.ToInt32(buf, 8 + 24 * i) == disk) { letters.Add(root); break; }
                }
            }
            return letters;
        }

        /// <summary>Opens the drive read-only as a seekable stream that serves any read from sector-aligned reads.</summary>
        public static AlignedStream Open(int index)
        {
            SafeFileHandle h = CreateFile(@"\\.\PhysicalDrive" + index, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException(err == 5 ? "Reading a whole drive needs administrator rights." : new Win32Exception(err).Message);
            }
            long length;
            byte[] outBuf = new byte[8];
            int returned;
            if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outBuf, 8, out returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException("Could not read the drive size: " + new Win32Exception(err).Message);
            }
            length = BitConverter.ToInt64(outBuf, 0);
            return new AlignedStream(new FileStream(h, FileAccess.Read, 4096, false), length);
        }

        /// <summary>Opens a volume ("C:\") read-only, for reading its file system directly. Needs administrator rights.</summary>
        public static AlignedStream OpenVolume(string root)
        {
            string letter = root.Substring(0, 2);
            SafeFileHandle h = CreateFile(@"\\.\" + letter, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException(err == 5 ? "Reading a drive directly needs administrator rights." : new Win32Exception(err).Message);
            }
            byte[] outBuf = new byte[8];
            int returned;
            if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outBuf, 8, out returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new IOException("Could not read the size of " + letter + ": " + new Win32Exception(err).Message);
            }
            return new AlignedStream(new FileStream(h, FileAccess.Read, 4096, false), BitConverter.ToInt64(outBuf, 0));
        }

        private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    }

    /// <summary>
    /// Raw drives only accept reads that start and end on sector boundaries. This stream turns any Seek/Read into
    /// aligned 1 MB block reads, so the scanner and the viewer can treat a drive like a file.
    /// </summary>
    public sealed class AlignedStream : Stream
    {
        private const int Block = 1 << 20;
        private readonly FileStream inner;
        private readonly long length;
        private readonly byte[] block = new byte[Block];
        private long blockStart = -1;
        private int blockLen;
        private long pos;

        public AlignedStream(FileStream inner, long length)
        {
            this.inner = inner;
            this.length = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position { get { return pos; } set { pos = value; } }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin origin)
        {
            pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? pos + offset : length + offset;
            return pos;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (count > 0 && pos < length)
            {
                long bs = pos - (pos % Block);
                if (bs != blockStart)
                {
                    inner.Position = bs;
                    int want = (int)Math.Min(Block, length - bs);
                    int got = 0;
                    while (got < want)
                    {
                        int n = inner.Read(block, got, want - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    blockStart = bs;
                    blockLen = got;
                    if (got == 0) break;
                }
                int inBlock = (int)(pos - blockStart);
                int n2 = Math.Min(count, blockLen - inBlock);
                if (n2 <= 0) break;
                Buffer.BlockCopy(block, inBlock, buffer, offset, n2);
                pos += n2; offset += n2; count -= n2; total += n2;
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Where a drive scan found the text.</summary>
    public sealed class DriveHit
    {
        public long Offset;
        public TextForm Form;
        public string Preview;
    }

    /// <summary>
    /// Reads a whole drive once: counts which 4 KB sectors hold any data at all (a wiped drive is all zeros), and
    /// optionally collects every place a text appears. Read-only.
    /// </summary>
    public sealed class DriveScan
    {
        public const int MaxHits = 500;
        public const int Sector = 4096;

        public long Length;
        public long BytesRead;
        public long DataSectors;          // sectors with at least one non-zero byte
        public long FirstData = -1;       // offset of the first such sector
        public readonly List<DriveHit> Hits = new List<DriveHit>();
        public volatile bool Done;
        public string Error;

        private readonly BytePattern p8, p16;

        public DriveScan(BytePattern p8, BytePattern p16)
        {
            this.p8 = p8;
            this.p16 = p16;
        }

        public void Run(Stream s, CancellationToken ct)
        {
            Length = s.Length;
            const int chunk = 8 << 20;
            int overlap = Math.Max(p8 == null ? 0 : p8.MaxLength, p16 == null ? 0 : p16.MaxLength) + 2;
            byte[] buf = new byte[chunk + overlap];
            long bufStart = 0;
            int carry = 0;
            long next8 = 0, next16 = 0;
            s.Position = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int got = 0;
                while (got < chunk)
                {
                    int n = s.Read(buf, carry + got, chunk - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got == 0) break;
                CountData(buf, carry, got, bufStart + carry);
                int dataLen = carry + got;
                bool eof = bufStart + dataLen >= Length || got < chunk;
                int limit = eof ? dataLen : dataLen - overlap;

                for (int pass = 0; pass < 2 && Hits.Count < MaxHits; pass++)
                {
                    BytePattern p = pass == 0 ? p8 : p16;
                    if (p == null) continue;
                    long nextAbs = pass == 0 ? next8 : next16;
                    int from = (int)Math.Max(0, nextAbs - bufStart);
                    int end;
                    while (Hits.Count < MaxHits)
                    {
                        int st = p.Find(buf, from, limit, dataLen, out end);
                        if (st < 0) break;
                        DriveHit h = new DriveHit();
                        h.Offset = bufStart + st;
                        h.Form = p.Form;
                        h.Preview = Scanner.MakePreview(buf, st, end, dataLen, p.Form);
                        lock (Hits) Hits.Add(h);
                        from = Math.Max(end, st + 1);
                    }
                    nextAbs = bufStart + Math.Max(from, limit);
                    if (pass == 0) next8 = nextAbs; else next16 = nextAbs;
                }

                Interlocked.Add(ref BytesRead, got);
                if (eof) break;
                carry = Math.Min(overlap, dataLen);
                Buffer.BlockCopy(buf, dataLen - carry, buf, 0, carry);
                bufStart += dataLen - carry;
            }
        }

        /// <summary>Counts 4 KB sectors with any non-zero byte (8 bytes at a time).</summary>
        private unsafe void CountData(byte[] buf, int start, int count, long absStart)
        {
            fixed (byte* p0 = buf)
            {
                for (int off = 0; off < count; off += Sector)
                {
                    int len = Math.Min(Sector, count - off);
                    ulong* q = (ulong*)(p0 + start + off);
                    bool data = false;
                    int words = len / 8;
                    for (int k = 0; k < words; k++) if (q[k] != 0) { data = true; break; }
                    if (!data) for (int k = words * 8; k < len; k++) if (p0[start + off + k] != 0) { data = true; break; }
                    if (data)
                    {
                        DataSectors++;
                        if (FirstData < 0) FirstData = absStart + off;
                    }
                }
            }
        }
    }
}
