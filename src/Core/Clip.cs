using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FindAnything.Core
{
    /// <summary>
    /// Picture similarity. OpenAI's CLIP (ViT-B/32, 8-bit) turns a picture or a sentence into 512 numbers describing
    /// what is in it, which is how a description finds pictures. Meta's DINOv2-small turns a picture into 384 numbers
    /// describing how it looks, which is how a sample finds look-alikes. Numbers pointing the same way = alike. Runs on the CPU through
    /// ONNX Runtime; the model files live in %LOCALAPPDATA%\FindAnything\ai (downloaded once, with permission).
    /// </summary>
    public sealed class Clip : IDisposable
    {
        public const int Dim = 512;
        private const int Side = 224;
        private static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
        private static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindAnything", "ai"); }
        }

        public static readonly string[] Files = { "onnxruntime.dll", "vision_model_quantized.onnx", "text_model_quantized.onnx", "vocab.json", "merges.txt", LookModel };

        /// <summary>
        /// Meta's DINOv2-small (8-bit): judges how alike two pictures look (the same dog, the same place) far better
        /// than CLIP, which judges what kind of picture it is. Used for sample pictures; CLIP stays for descriptions.
        /// </summary>
        public const string LookModel = "dinov2_small_quantized.onnx";

        /// <summary>
        /// Measured cut-offs: one dog's 22 photos against 242 unrelated pictures, every photo as the sample in turn.
        /// DINOv2: 99.6% of same-dog pairs score 0.30 or more (lowest 0.245); no unrelated picture reached 0.22.
        /// </summary>
        public const float LookAlike = 0.25f;

        public static bool Installed
        {
            get
            {
                foreach (string f in Files) if (!File.Exists(Path.Combine(Folder, f))) return false;
                return true;
            }
        }

        /// <summary>Keep picture fingerprints on disk (off by default, like the OCR text).</summary>
        public static bool UseCache;

        private readonly Ort ort;
        private readonly Ort.Session vision;
        private Ort.Session text;
        private ClipTokenizer tokenizer;
        private readonly string imageOut;
        private readonly object textGate = new object(), lookGate = new object();
        private Ort.Session look;
        private readonly int threads;

        public Clip(int threads)
        {
            this.threads = threads;
            ort = new Ort(Path.Combine(Folder, "onnxruntime.dll"));
            vision = ort.Open(Path.Combine(Folder, "vision_model_quantized.onnx"), threads);
            imageOut = Pick(vision.Outputs, "image_embeds");
        }

        private static string Pick(string[] names, string want)
        {
            foreach (string n in names) if (n == want) return n;
            foreach (string n in names) if (n.Contains("embeds")) return n;
            return names[0];
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>The picture's fingerprint (unit length), from the cache when allowed.</summary>
        public float[] EmbedImage(string ioPath)
        {
            float[] cached = FromCache(ioPath, "clip");
            if (cached != null) return cached;
            float[] pixels = Preprocess(File.ReadAllBytes(ioPath));
            long[] shape;
            float[] e = ort.Run(vision, new[] { new Ort.Input { Name = vision.Inputs[0], Floats = pixels, Shape = new long[] { 1, 3, Side, Side } } }, imageOut, out shape);
            Normalize(e);
            ToCache(ioPath, "clip", e);
            return e;
        }

        private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f }, ImageNetStd = { 0.229f, 0.224f, 0.225f };

        /// <summary>
        /// How the picture looks, as DINOv2-small sees it: the class token of its last layer, unit length. Its
        /// preparation: shortest side 256, centre crop 224, ImageNet mean and spread.
        /// </summary>
        public float[] EmbedLook(string ioPath)
        {
            float[] cached = FromCache(ioPath, "look");
            if (cached != null) return cached;
            lock (lookGate)
                if (look == null) look = ort.Open(Path.Combine(Folder, LookModel), threads);
            float[] pixels = Preprocess(File.ReadAllBytes(ioPath), 256, ImageNetMean, ImageNetStd);
            long[] shape;
            float[] hidden = ort.Run(look, new[] { new Ort.Input { Name = look.Inputs[0], Floats = pixels, Shape = new long[] { 1, 3, Side, Side } } }, look.Outputs[0], out shape);
            int width = (int)shape[shape.Length - 1];
            float[] e = new float[width];
            Array.Copy(hidden, e, width);   // token 0 is the class token
            Normalize(e);
            ToCache(ioPath, "look", e);
            return e;
        }

        /// <summary>CLIP's preparation (see the overload).</summary>
        public static float[] Preprocess(byte[] file) { return Preprocess(file, Side, Mean, Std); }

        /// <summary>
        /// Shortest side to 'resize' (Windows' decoder does the resampling, upright per EXIF), centre crop 224 x 224,
        /// RGB scaled to 0..1, then normalised with the model's per-channel mean and spread.
        /// </summary>
        public static float[] Preprocess(byte[] file, int resize, float[] mean, float[] std)
        {
            BitmapDecoder dec = WinRt.Wait(BitmapDecoder.CreateAsync(WinRt.ToStream(file)));
            uint ow = dec.OrientedPixelWidth, oh = dec.OrientedPixelHeight;
            double k = (double)resize / Math.Min(ow, oh);
            uint sw = (uint)Math.Max(resize, Math.Round(ow * k)), sh = (uint)Math.Max(resize, Math.Round(oh * k));
            bool turned = ow != dec.PixelWidth;   // EXIF 90/270: the transform is given in stored (unturned) dimensions
            BitmapTransform tr = new BitmapTransform();
            tr.ScaledWidth = turned ? sh : sw;
            tr.ScaledHeight = turned ? sw : sh;
            tr.InterpolationMode = BitmapInterpolationMode.Fant;
            PixelDataProvider px = WinRt.Wait(dec.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, tr,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage));
            byte[] rgba = px.DetachPixelData();
            int w = (int)sw, h = (int)sh;   // the upright size: the turn is applied after the scaling
            int x0 = (w - Side) / 2, y0 = (h - Side) / 2;
            float[] o = new float[3 * Side * Side];
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    int s = ((y0 + y) * w + (x0 + x)) * 4;
                    int d = y * Side + x;
                    for (int c = 0; c < 3; c++) o[c * Side * Side + d] = (rgba[s + c] / 255f - mean[c]) / std[c];
                }
            return o;
        }

        // ------------------------------------------------------------------ text

        /// <summary>The fingerprint of a description ("dog in a costume").</summary>
        public float[] EmbedText(string sentence)
        {
            lock (textGate)
            {
                if (text == null)
                {
                    text = ort.Open(Path.Combine(Folder, "text_model_quantized.onnx"), Environment.ProcessorCount);
                    tokenizer = new ClipTokenizer(Path.Combine(Folder, "vocab.json"), Path.Combine(Folder, "merges.txt"));
                }
            }
            long[] ids = tokenizer.Encode(sentence);
            List<Ort.Input> inputs = new List<Ort.Input>();
            foreach (string name in text.Inputs)
            {
                long[] data = ids;
                if (name.Contains("mask")) { data = new long[ids.Length]; for (int i = 0; i < data.Length; i++) data[i] = 1; }
                inputs.Add(new Ort.Input { Name = name, Longs = data, Shape = new long[] { 1, ids.Length } });
            }
            long[] shape;
            float[] e = ort.Run(text, inputs.ToArray(), Pick(text.Outputs, "text_embeds"), out shape);
            Normalize(e);
            return e;
        }

        public static float Similarity(float[] a, float[] b)
        {
            float s = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
            return s;
        }

        private static void Normalize(float[] v)
        {
            double n = 0;
            foreach (float x in v) n += x * x;
            float k = (float)(1 / Math.Sqrt(Math.Max(n, 1e-12)));
            for (int i = 0; i < v.Length; i++) v[i] *= k;
        }

        // ------------------------------------------------------------------ cache (opt-in)

        public static string CacheFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindAnything", "pictures"); }
        }

        public static void ClearCache()
        {
            try { if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true); } catch (Exception) { }
        }

        private static string CachePath(string ioPath, string model)
        {
            FileInfo fi = new FileInfo(ioPath);
            string key = model + "|" + LongPath.Strip(fi.FullName).ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (SHA1 sha = SHA1.Create())
            {
                StringBuilder sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(key))) sb.Append(b.ToString("x2"));
                return Path.Combine(CacheFolder, sb.ToString(0, 2), sb.ToString() + ".bin");
            }
        }

        private static float[] FromCache(string ioPath, string model)
        {
            if (!UseCache) return null;
            try
            {
                string p = CachePath(ioPath, model);
                if (!File.Exists(p)) return null;
                byte[] b = File.ReadAllBytes(p);
                if (b.Length == 0 || b.Length % 4 != 0 || b.Length > 4096) return null;
                float[] e = new float[b.Length / 4];
                System.Buffer.BlockCopy(b, 0, e, 0, b.Length);
                return e;
            }
            catch (Exception) { return null; }
        }

        private static void ToCache(string ioPath, string model, float[] e)
        {
            if (!UseCache || e.Length == 0) return;
            try
            {
                string p = CachePath(ioPath, model);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                byte[] b = new byte[e.Length * 4];
                System.Buffer.BlockCopy(e, 0, b, 0, b.Length);
                File.WriteAllBytes(p, b);
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            ort.Close(vision);
            ort.Close(text);
            ort.Close(look);
            ort.Dispose();
        }
    }

    /// <summary>
    /// CLIP's byte-pair tokenizer: lower-cases, splits into words / single digits / punctuation runs, maps bytes to
    /// printable characters (GPT-2 scheme), then merges character pairs by rank until no listed merge applies.
    /// </summary>
    internal sealed class ClipTokenizer
    {
        private const int Start = 49406, End = 49407, MaxLen = 77;
        private readonly Dictionary<string, int> vocab = new Dictionary<string, int>();
        private readonly Dictionary<string, int> ranks = new Dictionary<string, int>();
        private readonly string[] byteChar = new string[256];
        private readonly Dictionary<string, string[]> memo = new Dictionary<string, string[]>();

        public ClipTokenizer(string vocabPath, string mergesPath)
        {
            ParseVocab(File.ReadAllText(vocabPath, Encoding.UTF8));
            string[] lines = File.ReadAllLines(mergesPath, Encoding.UTF8);
            int rank = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("#version") || l.Trim().Length == 0) continue;
                ranks[l] = rank++;
            }
            // GPT-2 bytes_to_unicode: printable bytes map to themselves, the rest to 256 + n.
            int extra = 0;
            for (int b = 0; b < 256; b++)
            {
                bool printable = (b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
                byteChar[b] = ((char)(printable ? b : 256 + extra++)).ToString();
            }
        }

        /// <summary>A flat {"token": id} JSON object.</summary>
        private void ParseVocab(string json)
        {
            int i = 0;
            while (true)
            {
                int q = json.IndexOf('"', i);
                if (q < 0) break;
                StringBuilder key = new StringBuilder();
                int j = q + 1;
                while (j < json.Length && json[j] != '"')
                {
                    char c = json[j];
                    if (c == '\\' && j + 1 < json.Length)
                    {
                        char e = json[++j];
                        if (e == 'u') { key.Append((char)int.Parse(json.Substring(j + 1, 4), NumberStyles.HexNumber)); j += 4; }
                        else if (e == 'n') key.Append('\n'); else if (e == 't') key.Append('\t'); else if (e == 'r') key.Append('\r');
                        else key.Append(e);
                    }
                    else key.Append(c);
                    j++;
                }
                int colon = json.IndexOf(':', j);
                int k = colon + 1;
                while (k < json.Length && (json[k] == ' ' || json[k] == '\n' || json[k] == '\r' || json[k] == '\t')) k++;
                int s = k;
                while (k < json.Length && char.IsDigit(json[k])) k++;
                vocab[key.ToString()] = int.Parse(json.Substring(s, k - s));
                i = k;
            }
        }

        public long[] Encode(string text)
        {
            List<long> ids = new List<long> { Start };
            foreach (string word in PreTokenize(text.ToLowerInvariant()))
            {
                StringBuilder mapped = new StringBuilder();
                foreach (byte b in Encoding.UTF8.GetBytes(word)) mapped.Append(byteChar[b]);
                foreach (string piece in Bpe(mapped.ToString()))
                {
                    int id;
                    if (vocab.TryGetValue(piece, out id)) ids.Add(id);
                }
                if (ids.Count >= MaxLen - 1) break;
            }
            if (ids.Count > MaxLen - 1) ids.RemoveRange(MaxLen - 1, ids.Count - (MaxLen - 1));
            ids.Add(End);
            return ids.ToArray();
        }

        /// <summary>Words of letters, single digits, runs of other symbols, and English contractions ('s, 're ...).</summary>
        private static IEnumerable<string> PreTokenize(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '\'')
                {
                    string rest = s.Substring(i);
                    string hit = null;
                    foreach (string con in new[] { "'s", "'t", "'re", "'ve", "'m", "'ll", "'d" })
                        if (rest.StartsWith(con)) { hit = con; break; }
                    if (hit != null) { yield return hit; i += hit.Length; continue; }
                }
                if (char.IsLetter(c))
                {
                    int j = i;
                    while (j < s.Length && char.IsLetter(s[j])) j++;
                    yield return s.Substring(i, j - i);
                    i = j;
                }
                else if (char.IsDigit(c)) { yield return c.ToString(); i++; }
                else
                {
                    int j = i;
                    while (j < s.Length && !char.IsWhiteSpace(s[j]) && !char.IsLetterOrDigit(s[j])) j++;
                    yield return s.Substring(i, j - i);
                    i = j;
                }
            }
        }

        private string[] Bpe(string token)
        {
            string[] cached;
            if (memo.TryGetValue(token, out cached)) return cached;
            List<string> word = new List<string>();
            for (int i = 0; i < token.Length; i++) word.Add(i == token.Length - 1 ? token[i] + "</w>" : token[i].ToString());
            while (word.Count > 1)
            {
                int best = int.MaxValue, at = -1;
                for (int i = 0; i + 1 < word.Count; i++)
                {
                    int r;
                    if (ranks.TryGetValue(word[i] + " " + word[i + 1], out r) && r < best) { best = r; at = i; }
                }
                if (at < 0) break;
                string a = word[at], b = word[at + 1];
                List<string> next = new List<string>();
                for (int i = 0; i < word.Count; i++)
                {
                    if (i + 1 < word.Count && word[i] == a && word[i + 1] == b) { next.Add(a + b); i++; }
                    else next.Add(word[i]);
                }
                word = next;
            }
            string[] result = word.ToArray();
            memo[token] = result;
            return result;
        }
    }

    /// <summary>Waiting on WinRT calls without the SDK's AsTask helpers (shared by OCR and the picture model).</summary>
    internal static class WinRt
    {
        public static T Wait<T>(IAsyncOperation<T> op)
        {
            int spins = 0;
            while (op.Status == AsyncStatus.Started) Thread.Sleep(spins++ < 50 ? 1 : 5);
            if (op.Status != AsyncStatus.Completed) throw new IOException("Windows could not read the picture" + (op.ErrorCode != null ? ": " + op.ErrorCode.Message : ""));
            return op.GetResults();
        }

        public static T Wait<T, P>(IAsyncOperationWithProgress<T, P> op)
        {
            int spins = 0;
            while (op.Status == AsyncStatus.Started) Thread.Sleep(spins++ < 50 ? 1 : 5);
            if (op.Status != AsyncStatus.Completed) throw new IOException("Windows could not finish" + (op.ErrorCode != null ? ": " + op.ErrorCode.Message : ""));
            return op.GetResults();
        }

        public static IRandomAccessStream ToStream(byte[] bytes)
        {
            InMemoryRandomAccessStream s = new InMemoryRandomAccessStream();
            DataWriter dw = new DataWriter(s);
            dw.WriteBytes(bytes);
            Wait(dw.StoreAsync());
            dw.DetachStream();
            s.Seek(0);
            return s;
        }
    }
}
