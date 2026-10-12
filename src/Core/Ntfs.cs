using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace FindAnything.Core
{
    /// <summary>A stretch of a file's data on the volume: Length clusters starting at Lcn (Lcn -1 = a hole, reads as zeros).</summary>
    public struct DataRun
    {
        public long Lcn, Length;
    }

    /// <summary>A deleted file found in the MFT.</summary>
    public sealed class DeletedFile
    {
        public long Record;
        public string Name;
        public string Folder;           // the folder it was in, rebuilt from the MFT (may be partly unknown)
        public long Size;
        public DateTime Modified, Created;
        public byte[] Resident;         // small files live inside the MFT record itself
        public List<DataRun> Runs = new List<DataRun>();
        public long ClustersInUse;      // clusters of its data that another file has taken since
        public long ClustersTotal;

        public string Path { get { return Folder.TrimEnd('\\') + "\\" + Name; } }

        /// <summary>2 = intact (nothing reused), 1 = partly overwritten, 0 = overwritten.</summary>
        public int Chance
        {
            get
            {
                if (Resident != null || ClustersTotal == 0) return 2;
                if (ClustersInUse == 0) return 2;
                return ClustersInUse >= ClustersTotal ? 0 : 1;
            }
        }
    }

    /// <summary>
    /// Reads an NTFS volume directly (read-only) to find deleted files and copy their data back out. The MFT is the
    /// volume's table of every file: a deleted file's record stays there, marked free, until the slot is reused, and its
    /// data stays on the volume until the clusters are reused - or, on an SSD, erased by TRIM.
    /// </summary>
    public sealed class NtfsVolume : IDisposable
    {
        private readonly Stream disk;
        public readonly string Root;          // "C:\"
        public int BytesPerSector, ClusterSize, RecordSize;
        public long TotalClusters, MftLcn;
        private List<DataRun> mftRuns;
        private byte[] bitmap;                // one bit per cluster: 1 = in use now

        public long RecordsRead, Records;
        public long Valid, Torn, DeletedSeen, NoData;   // what the last scan saw

        public NtfsVolume(Stream volume, string root)
        {
            disk = volume;
            Root = root;
            byte[] boot = ReadAt(0, 512);
            if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") throw new InvalidDataException(root + " is not an NTFS volume (FAT and exFAT drives are not supported).");
            BytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
            int spc = boot[0x0D];
            if (spc > 0x80) spc = 1 << (256 - spc);
            ClusterSize = BytesPerSector * spc;
            TotalClusters = BitConverter.ToInt64(boot, 0x28) / spc;
            MftLcn = BitConverter.ToInt64(boot, 0x30);
            sbyte cpr = (sbyte)boot[0x40];
            RecordSize = cpr > 0 ? cpr * ClusterSize : 1 << -cpr;
            if (BytesPerSector < 512 || ClusterSize < 512 || RecordSize < 512 || RecordSize > 65536) throw new InvalidDataException("Unexpected NTFS geometry on " + root + ".");

            // Record 0 is $MFT itself: its data runs say where the whole table is.
            byte[] rec0 = ReadAt(MftLcn * ClusterSize, RecordSize);
            if (!ApplyFixups(rec0, BytesPerSector)) throw new InvalidDataException("The MFT of " + root + " could not be read.");
            Parsed p0 = Parse(rec0);
            if (p0 == null || p0.Runs.Count == 0) throw new InvalidDataException("The MFT of " + root + " has no data runs.");
            mftRuns = p0.Runs;
            Records = p0.DataSize / RecordSize;
        }

        public void Dispose() { disk.Dispose(); }

        private byte[] ReadAt(long offset, int count)
        {
            byte[] b = new byte[count];
            disk.Position = offset;
            int got = 0;
            while (got < count)
            {
                int n = disk.Read(b, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return b;
        }

        // ------------------------------------------------------------------ records

        /// <summary>Undoes NTFS's torn-write protection: the last two bytes of each sector were swapped for a check value.</summary>
        public static bool ApplyFixups(byte[] rec, int sectorSize)
        {
            if (rec.Length < 48 || rec[0] != 'F' || rec[1] != 'I' || rec[2] != 'L' || rec[3] != 'E') return false;
            int usaOff = BitConverter.ToUInt16(rec, 4), usaCount = BitConverter.ToUInt16(rec, 6);
            if (usaOff + usaCount * 2 > rec.Length || usaCount < 2) return false;
            byte c0 = rec[usaOff], c1 = rec[usaOff + 1];
            for (int i = 1; i < usaCount; i++)
            {
                int end = i * sectorSize - 2;
                if (end + 1 >= rec.Length) break;
                if (rec[end] != c0 || rec[end + 1] != c1) return false;   // torn write: the record is not trustworthy
                rec[end] = rec[usaOff + i * 2];
                rec[end + 1] = rec[usaOff + i * 2 + 1];
            }
            return true;
        }

        /// <summary>Decodes a data run list: [header: offset size | length size][length][signed offset from the previous run].</summary>
        public static List<DataRun> DecodeRuns(byte[] b, int pos, int end)
        {
            List<DataRun> runs = new List<DataRun>();
            long lcn = 0;
            while (pos < end && b[pos] != 0)
            {
                int lenSize = b[pos] & 0x0F, offSize = b[pos] >> 4;
                pos++;
                if (lenSize == 0 || lenSize > 8 || offSize > 8 || pos + lenSize + offSize > end) break;
                long len = 0;
                for (int i = 0; i < lenSize; i++) len |= (long)b[pos + i] << (8 * i);
                pos += lenSize;
                DataRun r = new DataRun();
                r.Length = len;
                if (offSize == 0) r.Lcn = -1;   // sparse
                else
                {
                    long off = 0;
                    for (int i = 0; i < offSize; i++) off |= (long)b[pos + i] << (8 * i);
                    if ((b[pos + offSize - 1] & 0x80) != 0) off |= -1L << (8 * offSize);   // sign-extend
                    pos += offSize;
                    lcn += off;
                    r.Lcn = lcn;
                }
                runs.Add(r);
            }
            return runs;
        }

        private sealed class Parsed
        {
            public bool InUse, Directory, Extension;
            public ushort Sequence;
            public long Parent;
            public ushort ParentSequence;
            public string Name;
            public long DataSize;
            public byte[] Resident;
            public List<DataRun> Runs = new List<DataRun>();
            public bool HasData, DataElsewhere;
            public DateTime Created, Modified;
        }

        private static Parsed Parse(byte[] rec)
        {
            Parsed p = new Parsed();
            ushort flags = BitConverter.ToUInt16(rec, 0x16);
            p.InUse = (flags & 1) != 0;
            p.Directory = (flags & 2) != 0;
            p.Sequence = BitConverter.ToUInt16(rec, 0x10);
            p.Extension = BitConverter.ToInt64(rec, 0x20) != 0;
            int off = BitConverter.ToUInt16(rec, 0x14);
            int nameRank = -1;
            bool attrList = false;
            while (off + 16 <= rec.Length)
            {
                uint type = BitConverter.ToUInt32(rec, off);
                if (type == 0xFFFFFFFF) break;
                int len = (int)BitConverter.ToUInt32(rec, off + 4);
                if (len < 16 || off + len > rec.Length) break;
                bool nonResident = rec[off + 8] != 0;
                int nameLen = rec[off + 9];
                if (type == 0x20) attrList = true;
                if (!nonResident)
                {
                    int cLen = (int)BitConverter.ToUInt32(rec, off + 0x10), cOff = BitConverter.ToUInt16(rec, off + 0x14);
                    int c = off + cOff;
                    if (c + cLen > rec.Length) { off += len; continue; }
                    if (type == 0x10 && cLen >= 32)
                    {
                        p.Created = Time(rec, c);
                        p.Modified = Time(rec, c + 8);
                    }
                    else if (type == 0x30 && cLen >= 0x42)
                    {
                        int ns = rec[c + 0x41], nl = rec[c + 0x40];
                        int rank = ns == 2 ? 0 : ns == 0 ? 1 : 2;   // prefer the long (Win32) name over the 8.3 one
                        if (rank > nameRank && c + 0x42 + nl * 2 <= rec.Length)
                        {
                            nameRank = rank;
                            long pref = BitConverter.ToInt64(rec, c);
                            p.Parent = pref & 0xFFFFFFFFFFFFL;
                            p.ParentSequence = (ushort)(pref >> 48);
                            p.Name = Encoding.Unicode.GetString(rec, c + 0x42, nl * 2);
                        }
                    }
                    else if (type == 0x80 && nameLen == 0)
                    {
                        p.HasData = true;
                        p.DataSize = cLen;
                        p.Resident = new byte[cLen];
                        Buffer.BlockCopy(rec, c, p.Resident, 0, cLen);
                    }
                }
                else if (type == 0x80 && nameLen == 0)
                {
                    long startVcn = BitConverter.ToInt64(rec, off + 0x10);
                    if (startVcn == 0)
                    {
                        p.HasData = true;
                        int runOff = BitConverter.ToUInt16(rec, off + 0x20);
                        p.DataSize = BitConverter.ToInt64(rec, off + 0x30);
                        p.Runs = DecodeRuns(rec, off + runOff, off + len);
                        long lastVcn = BitConverter.ToInt64(rec, off + 0x18);
                        long covered = 0;
                        foreach (DataRun r in p.Runs) covered += r.Length;
                        if (covered < lastVcn + 1) p.DataElsewhere = true;   // the rest of the runs live in an extension record
                    }
                    else p.DataElsewhere = true;
                }
                off += len;
            }
            if (attrList && !p.HasData) p.DataElsewhere = true;
            return p;
        }

        private static DateTime Time(byte[] b, int at)
        {
            long ft = BitConverter.ToInt64(b, at);
            try { return ft > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime() : DateTime.MinValue; } catch (Exception) { return DateTime.MinValue; }
        }

        // ------------------------------------------------------------------ the search

        private sealed class Dir
        {
            public string Name;
            public long Parent;
            public ushort Sequence, ParentSequence;
            public bool InUse;
        }

        /// <summary>
        /// Reads the whole MFT (1 MB at a time) and returns every deleted file that still has its name and data map.
        /// accept(name) filters early, so huge volumes do not fill memory with files nobody asked for.
        /// </summary>
        public List<DeletedFile> FindDeleted(Func<string, bool> accept, CancellationToken ct)
        {
            Dictionary<long, Dir> dirs = new Dictionary<long, Dir>();
            List<KeyValuePair<DeletedFile, Parsed>> found = new List<KeyValuePair<DeletedFile, Parsed>>();
            int perChunk = Math.Max(1, (1 << 20) / RecordSize);
            long index = 0;
            foreach (DataRun run in mftRuns)
            {
                if (run.Lcn < 0) { index += run.Length * ClusterSize / RecordSize; continue; }
                long bytes = run.Length * ClusterSize, done = 0;
                while (done < bytes)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min((long)perChunk * RecordSize, bytes - done);
                    byte[] chunk = ReadAt(run.Lcn * ClusterSize + done, count);
                    for (int o = 0; o + RecordSize <= count; o += RecordSize, index++)
                    {
                        if (index >= Records) break;
                        byte[] rec = new byte[RecordSize];
                        Buffer.BlockCopy(chunk, o, rec, 0, RecordSize);
                        if (rec[0] != (byte)'F') continue;   // never used
                        if (!ApplyFixups(rec, BytesPerSector)) { Torn++; continue; }
                        Valid++;
                        Parsed p;
                        try { p = Parse(rec); } catch (Exception) { continue; }
                        if (p.Extension || p.Name == null) continue;
                        if (p.Directory)
                        {
                            Dir d = new Dir();
                            d.Name = p.Name; d.Parent = p.Parent; d.Sequence = p.Sequence; d.ParentSequence = p.ParentSequence; d.InUse = p.InUse;
                            dirs[index] = d;
                            continue;
                        }
                        if (p.InUse || index < 24) continue;   // 0-23: NTFS's own files
                        DeletedSeen++;
                        if (!p.HasData || p.DataElsewhere) { NoData++; continue; }
                        if (accept != null && !accept(p.Name)) continue;
                        DeletedFile f = new DeletedFile();
                        f.Record = index; f.Name = p.Name; f.Size = p.DataSize;
                        f.Created = p.Created; f.Modified = p.Modified;
                        f.Resident = p.Resident;
                        f.Runs = p.Runs;
                        found.Add(new KeyValuePair<DeletedFile, Parsed>(f, p));
                    }
                    done += count;
                    Interlocked.Exchange(ref RecordsRead, index);
                }
            }

            LoadBitmap(ct);
            List<DeletedFile> list = new List<DeletedFile>(found.Count);
            foreach (KeyValuePair<DeletedFile, Parsed> kv in found)
            {
                kv.Key.Folder = FolderOf(kv.Value.Parent, kv.Value.ParentSequence, dirs);
                CountInUse(kv.Key);
                list.Add(kv.Key);
            }
            return list;
        }

        /// <summary>Rebuilds a folder path by following parent links up to the root (record 5).</summary>
        private string FolderOf(long parent, ushort parentSeq, Dictionary<long, Dir> dirs)
        {
            List<string> parts = new List<string>();
            long at = parent;
            ushort seq = parentSeq;
            for (int depth = 0; depth < 64; depth++)
            {
                if (at == 5) { parts.Reverse(); return Root.TrimEnd('\\') + (parts.Count > 0 ? "\\" + string.Join("\\", parts.ToArray()) : "\\"); }
                Dir d;
                if (!dirs.TryGetValue(at, out d) || (d.Sequence != seq && d.Sequence != seq + 1))
                {
                    // the folder's record has been reused: its name is gone
                    parts.Reverse();
                    return Root.TrimEnd('\\') + "\\(unknown folder)" + (parts.Count > 0 ? "\\" + string.Join("\\", parts.ToArray()) : "");
                }
                parts.Add(d.Name);
                at = d.Parent;
                seq = d.ParentSequence;
            }
            return Root.TrimEnd('\\') + "\\(unknown folder)";
        }

        // ------------------------------------------------------------------ is the data still there?

        private void LoadBitmap(CancellationToken ct)
        {
            if (bitmap != null) return;
            byte[] rec = ReadRecord(6);   // $Bitmap
            Parsed p = rec == null ? null : Parse(rec);
            if (p == null || !p.HasData) return;
            using (MemoryStream ms = new MemoryStream())
            {
                CopyData(p.Resident, p.Runs, p.DataSize, ms, ct, null);
                bitmap = ms.ToArray();
            }
        }

        private byte[] ReadRecord(long index)
        {
            long vcnByte = index * RecordSize;
            foreach (DataRun r in mftRuns)
            {
                long bytes = r.Length * ClusterSize;
                if (vcnByte < bytes)
                {
                    if (r.Lcn < 0) return null;
                    byte[] rec = ReadAt(r.Lcn * ClusterSize + vcnByte, RecordSize);
                    return ApplyFixups(rec, BytesPerSector) ? rec : null;
                }
                vcnByte -= bytes;
            }
            return null;
        }

        private bool InUse(long lcn)
        {
            if (bitmap == null || lcn < 0) return false;
            long b = lcn >> 3;
            return b < bitmap.Length && (bitmap[b] & (1 << (int)(lcn & 7))) != 0;
        }

        private void CountInUse(DeletedFile f)
        {
            long need = (f.Size + ClusterSize - 1) / ClusterSize, total = 0, used = 0;
            foreach (DataRun r in f.Runs)
            {
                if (total >= need) break;
                long n = Math.Min(r.Length, need - total);
                if (r.Lcn >= 0)
                    for (long c = 0; c < n; c++) if (InUse(r.Lcn + c)) used++;
                total += n;
            }
            f.ClustersTotal = total;
            f.ClustersInUse = used;
        }

        // ------------------------------------------------------------------ reading it back

        /// <summary>Writes the file's data (as it is on the volume now) to output, up to its size.</summary>
        public void Recover(DeletedFile f, Stream output, CancellationToken ct, Action<long> progress)
        {
            CopyData(f.Resident, f.Runs, f.Size, output, ct, progress);
        }

        /// <summary>The first bytes of the file, for a preview.</summary>
        public byte[] Head(DeletedFile f, int max)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                CopyData(f.Resident, f.Runs, Math.Min(f.Size, max), ms, CancellationToken.None, null);
                return ms.ToArray();
            }
        }

        private void CopyData(byte[] resident, List<DataRun> runs, long size, Stream output, CancellationToken ct, Action<long> progress)
        {
            if (resident != null) { output.Write(resident, 0, (int)Math.Min(resident.Length, size)); return; }
            long left = size, written = 0;
            byte[] zeros = null;
            foreach (DataRun r in runs)
            {
                long runBytes = r.Length * ClusterSize;
                long at = 0;
                while (at < runBytes && left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(Math.Min(1 << 20, runBytes - at), left);
                    if (r.Lcn < 0)
                    {
                        if (zeros == null || zeros.Length < n) zeros = new byte[1 << 20];
                        output.Write(zeros, 0, n);
                    }
                    else output.Write(ReadAt(r.Lcn * ClusterSize + at, n), 0, n);
                    at += n; left -= n; written += n;
                    if (progress != null) progress(written);
                }
                if (left <= 0) break;
            }
        }

        /// <summary>True when the bytes are all zero: on an SSD that usually means TRIM has erased the data.</summary>
        public static bool AllZero(byte[] b)
        {
            foreach (byte x in b) if (x != 0) return false;
            return b.Length > 0;
        }
    }
}
