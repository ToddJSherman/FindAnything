using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace FindAnything.Core
{
    public enum SecretKind { ProductKey, LicenseKey, Serial, Password, Pin, ApiToken, PrivateKey, Card, Ssn, CredentialFile }

    /// <summary>Which detectors run. The groups match the chips in the window.</summary>
    public sealed class SecretOptions
    {
        public bool ProductKeys = true;   // key-shaped codes: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
        public bool Labeled = true;       // "License:", "Serial number:", "Activation code =" ...
        public bool Passwords = true;     // "Password:", "PIN:", "Wi-Fi password is ..."
        public bool Tokens = true;        // API keys and tokens with known prefixes, private key blocks
        public bool Financial = true;     // card numbers (checksum-valid) and US Social Security numbers
        public bool CredentialFiles = true;   // files that are credentials by what they are: SSH keys, .env, vaults, wallets...
    }

    /// <summary>One thing found on one line.</summary>
    public struct SecretSpan
    {
        public SecretKind Kind;
        public int Start, Length;   // in the line
        public string Label;        // what the line called it ("Serial number"), or the format name
    }

    /// <summary>
    /// Spots keys, serials, passwords and tokens in a line of text. Plain character checks, no regular
    /// expressions, so each rule reads as what it looks for.
    /// </summary>
    public static class SecretDetector
    {
        public static string KindName(SecretKind k)
        {
            switch (k)
            {
                case SecretKind.ProductKey: return "Product key";
                case SecretKind.LicenseKey: return "License key";
                case SecretKind.Serial: return "Serial number";
                case SecretKind.Password: return "Password";
                case SecretKind.Pin: return "PIN / code";
                case SecretKind.ApiToken: return "API token";
                case SecretKind.PrivateKey: return "Private key";
                case SecretKind.Card: return "Card number";
                case SecretKind.CredentialFile: return "Credential file";
                default: return "SSN";
            }
        }

        public static List<SecretSpan> Scan(string line, SecretOptions o)
        {
            List<SecretSpan> found = new List<SecretSpan>();
            if (string.IsNullOrEmpty(line) || line.Length < 4) return found;
            if (o.Tokens) { PrivateKeys(line, found); Tokens(line, found); }
            if (o.Labeled || o.Passwords) Labeled(line, o, found);
            if (o.ProductKeys) KeyShapes(line, found);
            if (o.Financial) { Cards(line, found); Ssns(line, found); }
            return RemoveOverlaps(found);
        }

        private static void Add(List<SecretSpan> l, SecretKind k, int start, int len, string label)
        {
            SecretSpan s = new SecretSpan();
            s.Kind = k; s.Start = start; s.Length = len; s.Label = label;
            l.Add(s);
        }

        /// <summary>Earlier detectors win where spans overlap (they run most specific first).</summary>
        private static List<SecretSpan> RemoveOverlaps(List<SecretSpan> all)
        {
            List<SecretSpan> keep = new List<SecretSpan>();
            foreach (SecretSpan s in all)
            {
                bool clash = false;
                foreach (SecretSpan k in keep)
                    if (s.Start < k.Start + k.Length && k.Start < s.Start + s.Length) { clash = true; break; }
                if (!clash) keep.Add(s);
            }
            keep.Sort(delegate(SecretSpan a, SecretSpan b) { return a.Start.CompareTo(b.Start); });
            return keep;
        }

        private static bool IsAlnum(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'); }
        private static bool IsUpperAlnum(char c) { return (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); }
        private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        private static bool IsTokenChar(char c) { return IsAlnum(c) || c == '_' || c == '-'; }

        // ------------------------------------------------------------------ private keys

        private static void PrivateKeys(string line, List<SecretSpan> f)
        {
            int b = line.IndexOf("-----BEGIN", StringComparison.Ordinal);
            if (b >= 0 && line.IndexOf("PRIVATE KEY", b, StringComparison.Ordinal) > b)
            {
                int e = line.IndexOf("-----", b + 10, StringComparison.Ordinal);
                e = e < 0 ? line.Length : Math.Min(line.Length, e + 5);
                Add(f, SecretKind.PrivateKey, b, e - b, "Private key block");
            }
            int p = line.IndexOf("PuTTY-User-Key-File-", StringComparison.Ordinal);
            if (p >= 0) Add(f, SecretKind.PrivateKey, p, line.Length - p, "PuTTY private key");
        }

        // ------------------------------------------------------------------ tokens with known prefixes

        private static readonly string[][] Prefixes =
        {
            // prefix, name, minimum length of the part after the prefix, allowed chars ("a" alnum, "t" token chars, "u" upper+digits)
            new[] { "github_pat_", "GitHub token", "40", "t" },
            new[] { "ghp_", "GitHub token", "30", "a" }, new[] { "gho_", "GitHub token", "30", "a" },
            new[] { "ghu_", "GitHub token", "30", "a" }, new[] { "ghs_", "GitHub token", "30", "a" },
            new[] { "ghr_", "GitHub token", "30", "a" },
            new[] { "glpat-", "GitLab token", "20", "t" },
            new[] { "sk-ant-", "Anthropic API key", "20", "t" },
            new[] { "sk_live_", "Stripe secret key", "16", "a" }, new[] { "rk_live_", "Stripe key", "16", "a" },
            new[] { "sk-proj-", "OpenAI API key", "20", "t" },
            new[] { "xoxb-", "Slack token", "10", "t" }, new[] { "xoxp-", "Slack token", "10", "t" },
            new[] { "xoxa-", "Slack token", "10", "t" }, new[] { "xoxr-", "Slack token", "10", "t" },
            new[] { "xoxs-", "Slack token", "10", "t" },
            new[] { "AKIA", "AWS access key", "16", "u" }, new[] { "ASIA", "AWS access key", "16", "u" },
            new[] { "AIza", "Google API key", "35", "t" },
            new[] { "npm_", "npm token", "36", "a" },
            new[] { "SG.", "SendGrid key", "60", "t." },
            new[] { "eyJ", "JWT", "40", "t." },
            new[] { "sk-", "API secret key", "40", "t" },
        };

        private static void Tokens(string line, List<SecretSpan> f)
        {
            foreach (string[] p in Prefixes)
            {
                int from = 0;
                while (from < line.Length)
                {
                    int i = line.IndexOf(p[0], from, StringComparison.Ordinal);
                    if (i < 0) break;
                    from = i + 1;
                    if (i > 0 && IsTokenChar(line[i - 1])) continue;   // must start a word
                    int min = int.Parse(p[2]);
                    string allowed = p[3];
                    int j = i + p[0].Length;
                    while (j < line.Length && Allowed(line[j], allowed)) j++;
                    int body = j - i - p[0].Length;
                    if (body < min) continue;
                    if (p[1] == "JWT" && line.Substring(i, j - i).Split('.').Length < 3) continue;
                    if (p[1] == "API secret key" && !Mixed(line, i + 3, j)) continue;
                    Add(f, SecretKind.ApiToken, i, j - i, p[1]);
                    from = j;
                }
            }
        }

        private static bool Allowed(char c, string kind)
        {
            switch (kind)
            {
                case "a": return IsAlnum(c);
                case "u": return IsUpperAlnum(c);
                case "t.": return IsTokenChar(c) || c == '.';
                default: return IsTokenChar(c);
            }
        }

        /// <summary>Upper, lower and digits all present: random-looking, not a word.</summary>
        private static bool Mixed(string s, int a, int b)
        {
            bool up = false, low = false, dig = false;
            for (int i = a; i < b; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') up = true; else if (c >= 'a' && c <= 'z') low = true; else if (IsDigit(c)) dig = true;
            }
            return up && low && dig;
        }

        // ------------------------------------------------------------------ labelled values

        private static readonly string[][] Labels =
        {
            // label words (lower case), kind, label shown
            new[] { "product key", "L", "Product key" }, new[] { "license key", "L", "License key" },
            new[] { "licence key", "L", "License key" }, new[] { "activation key", "L", "Activation key" },
            new[] { "activation code", "L", "Activation code" }, new[] { "registration key", "L", "Registration key" },
            new[] { "registration code", "L", "Registration code" }, new[] { "reg code", "L", "Registration code" },
            new[] { "reg key", "L", "Registration key" }, new[] { "cd key", "L", "CD key" }, new[] { "cdkey", "L", "CD key" },
            new[] { "unlock key", "L", "Unlock key" }, new[] { "redeem code", "L", "Redeem code" },
            new[] { "redemption code", "L", "Redeem code" }, new[] { "gift card", "L", "Gift card" },
            new[] { "voucher code", "L", "Voucher" }, new[] { "license", "L", "License" }, new[] { "licence", "L", "License" },
            new[] { "key", "L", "Key" },
            new[] { "serial number", "S", "Serial number" }, new[] { "serial no", "S", "Serial number" },
            new[] { "serial", "S", "Serial number" }, new[] { "s/n", "S", "Serial number" }, new[] { "sn", "S", "Serial number" },
            new[] { "imei", "S", "IMEI" }, new[] { "service tag", "S", "Service tag" },
            new[] { "wi-fi password", "P", "Wi-Fi password" }, new[] { "wifi password", "P", "Wi-Fi password" },
            new[] { "network key", "P", "Wi-Fi key" }, new[] { "wpa key", "P", "Wi-Fi key" }, new[] { "wpa2 key", "P", "Wi-Fi key" },
            new[] { "password", "P", "Password" }, new[] { "passwd", "P", "Password" }, new[] { "passcode", "P", "Passcode" },
            new[] { "passphrase", "P", "Passphrase" }, new[] { "pass", "P", "Password" }, new[] { "pwd", "P", "Password" },
            new[] { "pw", "P", "Password" },
            new[] { "pin code", "N", "PIN" }, new[] { "pin", "N", "PIN" }, new[] { "security code", "N", "Security code" },
            new[] { "unlock code", "N", "Unlock code" }, new[] { "access code", "N", "Access code" },
            new[] { "alarm code", "N", "Alarm code" }, new[] { "door code", "N", "Door code" }, new[] { "gate code", "N", "Gate code" },
            new[] { "lock code", "N", "Lock code" }, new[] { "garage code", "N", "Garage code" }, new[] { "combination", "N", "Combination" },
            new[] { "api key", "T", "API key" }, new[] { "apikey", "T", "API key" }, new[] { "api_key", "T", "API key" },
            new[] { "access key", "T", "Access key" }, new[] { "secret key", "T", "Secret key" },
            new[] { "client secret", "T", "Client secret" }, new[] { "secret", "T", "Secret" }, new[] { "token", "T", "Token" },
        };

        private static void Labeled(string line, SecretOptions o, List<SecretSpan> f)
        {
            string low = line.ToLowerInvariant();
            // Separators that put a value after a label: ':' '=' and the word " is ".
            for (int i = 0; i < line.Length; i++)
            {
                int valueStart;
                char c = line[i];
                if (c == ':' || c == '=') valueStart = i + 1;
                else if (c == ' ' && i + 4 <= line.Length && low.Substring(i, 4) == " is ") valueStart = i + 4;
                else continue;
                if (c == ':' && i + 1 < line.Length && line[i + 1] == '/') continue;   // http://, not a label
                if (c == '=' && i + 1 < line.Length && line[i + 1] == '=') { i++; continue; }

                string label = LabelBefore(low, i);
                if (label.Length == 0) continue;
                string[] match = null;
                foreach (string[] l in Labels)
                {
                    if (label == l[0] || label.EndsWith(" " + l[0])) { match = l; break; }
                }
                if (match == null) continue;
                bool isPassword = match[1] == "P" || match[1] == "N";
                if (isPassword ? !o.Passwords : !o.Labeled) continue;
                if (match[1] == "T" && !o.Labeled) continue;

                int vs, ve;
                if (!ValueAfter(line, valueStart, out vs, out ve)) continue;
                string value = line.Substring(vs, ve - vs);
                if (Placeholder(value)) continue;
                if (match[1] == "N" && !PinLike(value)) continue;
                if ((match[0] == "key" || match[0] == "sn" || match[0] == "pw" || match[0] == "pass") && ve - vs < 4) continue;
                SecretKind kind = match[1] == "L" ? SecretKind.LicenseKey : match[1] == "S" ? SecretKind.Serial :
                                  match[1] == "P" ? SecretKind.Password : match[1] == "N" ? SecretKind.Pin : SecretKind.ApiToken;
                Add(f, kind, vs, ve - vs, match[2]);
                i = ve;
            }
        }

        /// <summary>The words just before a separator, normalised: "Windows 10 Pro Product_Key" gives "pro product key".</summary>
        private static string LabelBefore(string low, int sep)
        {
            int end = sep;
            while (end > 0 && (low[end - 1] == ' ' || low[end - 1] == '"' || low[end - 1] == '\'' || low[end - 1] == '\t')) end--;
            int start = end;
            while (start > 0 && end - start < 40)
            {
                char ch = low[start - 1];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '/' || ch == ' ' || ch == '.') start--;
                else break;
            }
            if (end <= start) return "";
            string[] words = low.Substring(start, end - start).Replace('_', ' ').Replace('.', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int take = Math.Min(4, words.Length);
            return string.Join(" ", words, words.Length - take, take);
        }
        private static bool ValueAfter(string line, int from, out int vs, out int ve)
        {
            vs = from;
            while (vs < line.Length && (line[vs] == ' ' || line[vs] == '\t')) vs++;
            bool quoted = vs < line.Length && (line[vs] == '"' || line[vs] == '\'');
            char q = quoted ? line[vs] : '\0';
            if (quoted) vs++;
            ve = vs;
            if (quoted) { while (ve < line.Length && line[ve] != q) ve++; }
            else
            {
                while (ve < line.Length && line[ve] != '\r' && line[ve] != '\n') ve++;
                // stop before trailing punctuation or a following "key: value" pair separated by two spaces / tab / comma+label
                int cut = line.IndexOf("  ", vs, ve - vs, StringComparison.Ordinal);
                int tab = line.IndexOf('\t', vs, ve - vs);
                if (tab >= 0 && (cut < 0 || tab < cut)) cut = tab;
                if (cut > vs) ve = cut;
                while (ve > vs && (line[ve - 1] == ' ' || line[ve - 1] == ',' || line[ve - 1] == ';' || line[ve - 1] == '.')) ve--;
            }
            if (ve - vs > 200) ve = vs + 200;
            return ve - vs >= 3;
        }

        private static bool Placeholder(string v)
        {
            string s = v.Trim().ToLowerInvariant();
            if (s.Length < 3) return true;
            string[] none = { "none", "n/a", "null", "nil", "true", "false", "yes", "no", "required", "optional", "unknown", "your", "xxx", "***", "...", "todo", "changeme", "example", "string", "undefined" };
            foreach (string n in none) if (s == n || s.StartsWith(n + " ")) return true;
            if (s.StartsWith("<") || s.StartsWith("${") || s.StartsWith("{{") || s.StartsWith("%") || s.StartsWith("$(")) return true;
            if (s.StartsWith("os.") || s.StartsWith("process.env") || s.StartsWith("env[") || s.StartsWith("environ")) return true;
            bool allSame = true;
            foreach (char c in s) if (c != s[0]) { allSame = false; break; }
            return allSame;
        }

        private static bool PinLike(string v)
        {
            int digits = 0;
            foreach (char c in v) { if (IsDigit(c)) digits++; else if (c != ' ' && c != '-') return false; }
            return digits >= 4 && digits <= 12;
        }

        // ------------------------------------------------------------------ key-shaped codes

        /// <summary>
        /// Codes in dash-separated groups: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (Windows, Office, games), ABCD-EFGH-1234-5678,
        /// 1234-5678-9012-3456-7890-1234 (some serials). At least 3 equal-length groups of 4-8 upper-case letters and
        /// digits, with both letters and digits (or 5+ groups of digits). Dates, GUIDs and phone numbers do not fit.
        /// </summary>
        private static void KeyShapes(string line, List<SecretSpan> f)
        {
            int n = line.Length;
            int i = 0;
            while (i < n)
            {
                // A key starts a word: not after a letter, digit or dash.
                if (!IsUpperAlnum(line[i]) || (i > 0 && (IsAlnum(line[i - 1]) || line[i - 1] == '-'))) { i++; continue; }
                int j = i, groups = 0, groupLen = -1, keyEnd = i;
                bool letters = false, digits = false;
                while (true)
                {
                    int g = j;
                    bool gl = false, gd = false;
                    while (j < n && IsUpperAlnum(line[j])) { if (IsDigit(line[j])) gd = true; else gl = true; j++; }
                    int len = j - g;
                    if (len < 4 || len > 8 || (groupLen >= 0 && len != groupLen)) break;
                    groupLen = len;
                    groups++;
                    letters |= gl;
                    digits |= gd;
                    keyEnd = j;
                    if (j + 1 < n && line[j] == '-' && IsUpperAlnum(line[j + 1])) { j++; continue; }
                    break;
                }
                // The key must end the word too (a GUID's odd-length last group, for example, disqualifies it).
                bool wordEnd = keyEnd >= n || !(IsAlnum(line[keyEnd]) || (line[keyEnd] == '-' && keyEnd + 1 < n && IsAlnum(line[keyEnd + 1])));
                bool shape = (letters && digits) || (digits && !letters && groups >= 5) || (groups >= 5 && groupLen == 5);
                if (groups >= 3 && wordEnd && shape)
                    Add(f, SecretKind.ProductKey, i, keyEnd - i, groups == 5 && groupLen == 5 ? "Product key" : "Key / serial");
                i = Math.Max(i + 1, j);
            }
        }
        // ------------------------------------------------------------------ cards and SSNs

        private static void Cards(string line, List<SecretSpan> f)
        {
            int i = 0;
            while (i < line.Length)
            {
                if (!IsDigit(line[i]) || (i > 0 && IsAlnum(line[i - 1]))) { i++; continue; }
                StringBuilder digits = new StringBuilder();
                int j = i, lastDigit = i;
                while (j < line.Length && digits.Length <= 19)
                {
                    if (IsDigit(line[j])) { digits.Append(line[j]); lastDigit = j; j++; }
                    else if ((line[j] == ' ' || line[j] == '-') && j + 1 < line.Length && IsDigit(line[j + 1]) && digits.Length > 0) j++;
                    else break;
                }
                int end = lastDigit + 1;
                string d = digits.ToString();
                if (d.Length >= 13 && d.Length <= 19 && (end >= line.Length || !IsAlnum(line[end])) && CardPrefix(d) && Luhn(d))
                    Add(f, SecretKind.Card, i, end - i, "Card number");
                i = Math.Max(i + 1, end);
            }
        }

        private static bool CardPrefix(string d)
        {
            if (d[0] == '4') return d.Length == 13 || d.Length == 16 || d.Length == 19;                    // Visa
            int two = int.Parse(d.Substring(0, 2)), four = int.Parse(d.Substring(0, 4));
            if (two >= 51 && two <= 55 || four >= 2221 && four <= 2720) return d.Length == 16;              // Mastercard
            if (two == 34 || two == 37) return d.Length == 15;                                               // Amex
            if (four == 6011 || two == 65) return d.Length >= 16;                                            // Discover
            return false;
        }

        public static bool Luhn(string d)
        {
            int sum = 0;
            bool dbl = false;
            for (int i = d.Length - 1; i >= 0; i--)
            {
                int n = d[i] - '0';
                if (dbl) { n *= 2; if (n > 9) n -= 9; }
                sum += n;
                dbl = !dbl;
            }
            return sum % 10 == 0;
        }

        private static void Ssns(string line, List<SecretSpan> f)
        {
            for (int i = 0; i + 11 <= line.Length; i++)
            {
                if (i > 0 && (IsAlnum(line[i - 1]) || line[i - 1] == '-')) continue;
                if (!(IsDigit(line[i]) && IsDigit(line[i + 1]) && IsDigit(line[i + 2]) && line[i + 3] == '-' && IsDigit(line[i + 4]) &&
                      IsDigit(line[i + 5]) && line[i + 6] == '-' && IsDigit(line[i + 7]) && IsDigit(line[i + 8]) && IsDigit(line[i + 9]) && IsDigit(line[i + 10])))
                    continue;
                if (i + 11 < line.Length && (IsAlnum(line[i + 11]) || line[i + 11] == '-')) continue;
                int area = int.Parse(line.Substring(i, 3)), group = int.Parse(line.Substring(i + 4, 2)), serial = int.Parse(line.Substring(i + 7, 4));
                if (area == 0 || area == 666 || area >= 900 || group == 0 || serial == 0) continue;
                Add(f, SecretKind.Ssn, i, 11, "SSN");
                i += 10;
            }
        }
    }

    /// <summary>A key/secret found in a file.</summary>
    public sealed class SecretFinding
    {
        public SecretKind Kind;
        public string Label;
        public string Value;
        public string Line;        // the line it sits on (trimmed to 300 characters around the value)
        public int ValueStart;     // in Line
        public long LineNumber;
        public int Severity;       // 3 high, 2 medium, 1 low (SecretAdvice.Rate)
        public string Advice;      // one line: what to do about it
    }

    /// <summary>
    /// Files that are credentials by what they are, whatever is inside: private keys, token files, password vaults,
    /// wallets, VPN and remote-desktop files. Matched on the name (and folder), never on a guess about the contents.
    /// </summary>
    public static class CredentialFiles
    {
        /// <summary>A label for the file when it is a credential file, else null. Severity: 3 high, 2 medium, 1 low.</summary>
        public static string Check(string path, out int severity)
        {
            severity = 0;
            string name = Path.GetFileName(path).ToLowerInvariant();
            string folder = (Path.GetDirectoryName(path) ?? "").ToLowerInvariant();
            string ext = Path.GetExtension(name);
            if (name == "id_rsa" || name == "id_dsa" || name == "id_ecdsa" || name == "id_ed25519" || name == "id_ecdsa_sk" || name == "id_ed25519_sk")
            { severity = 3; return "SSH private key"; }
            if (ext == ".ppk") { severity = 3; return "PuTTY private key"; }
            if (name == ".git-credentials") { severity = 3; return "Git saved credentials"; }
            if (name == "credentials" && folder.EndsWith("\\.aws")) { severity = 3; return "AWS access keys"; }
            if (name == "wallet.dat") { severity = 3; return "Crypto wallet"; }
            if (name == ".env" || (name.StartsWith(".env.") && name != ".env.example" && name != ".env.sample" && name != ".env.template"))
            { severity = 2; return "Environment secrets file"; }
            if (name == ".npmrc" || name == ".pypirc" || name == ".netrc" || name == "_netrc" || name == ".dockercfg" || (name == "config.json" && folder.EndsWith("\\.docker")))
            { severity = 2; return "Package / registry login file"; }
            if (name == "accesstokens.json" && folder.EndsWith("\\.azure")) { severity = 3; return "Azure access tokens"; }
            if (ext == ".pfx" || ext == ".p12") { severity = 2; return "Certificate with private key"; }
            if (ext == ".ovpn") { severity = 2; return "VPN profile"; }
            if (ext == ".rdp") { severity = 1; return "Remote Desktop file"; }
            if (ext == ".kdbx" || ext == ".kdb" || ext == ".1pif" || ext == ".opvault" || ext == ".psafe3") { severity = 1; return "Password vault (encrypted)"; }
            if (ext == ".keystore" || ext == ".jks") { severity = 2; return "Java keystore"; }
            return null;
        }
    }

    /// <summary>How bad a finding is and what to do about it, for the Sensitive data sweep.</summary>
    public static class SecretAdvice
    {
        public static void Rate(SecretFinding f, string path)
        {
            int s;
            string a;
            switch (f.Kind)
            {
                case SecretKind.PrivateKey: s = 3; a = "Private key in a file: keep it only where it is used, readable by your account alone, with a passphrase. Delete stray copies."; break;
                case SecretKind.ApiToken: s = 3; a = "Token in plain text: revoke or rotate it at the provider, then keep it in an environment variable or secret store."; break;
                case SecretKind.Password: s = 3; a = "Password in plain text: move it into a password manager, delete this copy, and change it if the file was ever shared."; break;
                case SecretKind.Card: s = 3; a = "Card number in plain text: delete it or keep it encrypted. Card numbers should never sit in ordinary files."; break;
                case SecretKind.Ssn: s = 3; a = "Social Security number in plain text: delete it or keep it encrypted (identity theft risk)."; break;
                case SecretKind.Pin: s = 2; a = "PIN or code in plain text: keep it in a password manager instead."; break;
                case SecretKind.CredentialFile: s = Math.Max(1, f.Severity); a = CredentialAdvice(f.Label); break;
                default: s = 1; a = "Licence key or serial: fine to keep, safer in a password manager than in a plain file."; break;
            }
            // Cloud-synced folders put the file on someone else's servers too.
            string p = (path ?? "").ToLowerInvariant();
            if (s < 3 && f.Kind != SecretKind.ProductKey && f.Kind != SecretKind.LicenseKey && f.Kind != SecretKind.Serial &&
                (p.Contains("\\onedrive") || p.Contains("\\dropbox\\") || p.Contains("\\google drive\\") || p.Contains("\\icloud")))
            {
                s++;
                a += " It is in a cloud-synced folder, so it is on the provider's servers too.";
            }
            f.Severity = s;
            f.Advice = a;
        }

        private static string CredentialAdvice(string label)
        {
            switch (label)
            {
                case "SSH private key": case "PuTTY private key":
                    return "Private key file: keep it only in your .ssh folder with a passphrase; delete copies elsewhere (Downloads, backups, USB sticks).";
                case "Git saved credentials": return "Git stores this password in plain text: switch to Git Credential Manager and delete this file.";
                case "AWS access keys": return "Long-lived AWS keys: prefer short-lived credentials (aws sso login); rotate these if the file was ever copied.";
                case "Crypto wallet": return "Wallet file: make sure it is encrypted with a strong passphrase and backed up offline.";
                case "Environment secrets file": return "Secrets file: never commit it or share it; keep it out of synced folders and backups you do not control.";
                case "Package / registry login file": return "Contains a registry login or token: check it for tokens and rotate any that left this PC.";
                case "Azure access tokens": return "Cached Azure tokens: sign out (az logout) on machines you do not control.";
                case "Certificate with private key": return "Certificate with its private key: make sure it has a strong password and is stored only where needed.";
                case "VPN profile": return "VPN profile: may embed keys or a password; keep it private.";
                case "Remote Desktop file": return "Remote Desktop file: may hold a saved password; remove saved credentials you no longer need.";
                case "Java keystore": return "Keystore: make sure it has a strong password.";
                default: return "Password vault: encrypted, fine to keep. Use a strong master password and keep a backup.";
            }
        }
    }

    /// <summary>Reads a text stream line by line (any size: long lines are cut into pieces) and runs the detectors.</summary>
    public static class SecretScanner
    {
        public const int MaxPerFile = 200;
        private const int MaxLine = 8192;

        /// <summary>True when the first bytes look like text (no NULs, or UTF-16 with its zero high bytes).</summary>
        public static bool LooksLikeText(byte[] head, int n)
        {
            if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return true;
            int zeros = 0, oddZeros = 0;
            for (int i = 0; i < n; i++) if (head[i] == 0) { zeros++; if ((i & 1) == 1) oddZeros++; }
            if (zeros == 0) return true;
            return n > 64 && oddZeros > n / 3 && zeros - oddZeros < n / 50;   // UTF-16 LE without a BOM
        }

        public static List<SecretFinding> Scan(Stream s, SecretOptions o, Encoding forced, CancellationToken ct)
        {
            List<SecretFinding> found = new List<SecretFinding>();
            Encoding enc = forced;
            if (enc == null)
            {
                byte[] head = new byte[4096];
                int n = s.Read(head, 0, head.Length);
                s.Position = 0;
                bool utf16 = n >= 2 && head[0] == 0xFF && head[1] == 0xFE || (n > 64 && LooksLikeText(head, n) && Array.IndexOf<byte>(head, 0, 0, n) >= 0);
                enc = utf16 ? Encoding.Unicode : new UTF8Encoding(false, false);
            }
            using (StreamReader r = new StreamReader(s, enc, true, 1 << 16))
            {
                char[] buf = new char[1 << 16];
                StringBuilder line = new StringBuilder();
                long lineNo = 1;
                int got;
                while ((got = r.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < got; i++)
                    {
                        char c = buf[i];
                        if (c == '\n' || line.Length >= MaxLine)
                        {
                            Check(line.ToString(), lineNo, o, found);
                            line.Length = 0;
                            if (c == '\n') lineNo++;
                            else line.Append(c);
                            if (found.Count >= MaxPerFile) return found;
                        }
                        else if (c != '\r') line.Append(c);
                    }
                }
                if (line.Length > 0) Check(line.ToString(), lineNo, o, found);
            }
            return found;
        }

        private static void Check(string line, long lineNo, SecretOptions o, List<SecretFinding> found)
        {
            List<SecretSpan> spans = SecretDetector.Scan(line, o);
            foreach (SecretSpan sp in spans)
            {
                SecretFinding f = new SecretFinding();
                f.Kind = sp.Kind;
                f.Label = sp.Label;
                f.Value = line.Substring(sp.Start, sp.Length);
                int from = Math.Max(0, sp.Start - 120);
                int to = Math.Min(line.Length, sp.Start + sp.Length + 120);
                f.Line = line.Substring(from, to - from).Replace('\t', ' ');
                f.ValueStart = sp.Start - from;
                f.LineNumber = lineNo;
                found.Add(f);
            }
        }
    }
}
